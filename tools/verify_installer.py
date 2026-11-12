#!/usr/bin/env python3
"""Exercise a built payload in isolated storage and HKCU test keys."""

from __future__ import annotations

import argparse
from contextlib import ExitStack
import json
import hashlib
import marshal
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request
import uuid
from unittest import mock
import winreg

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))
import installer


def verify_archive_privacy(payload: Path, setup: Path) -> None:
    from PyInstaller.archive.readers import CArchiveReader

    identities = [value for value in (
        os.environ.get("USERNAME"), os.environ.get("COMPUTERNAME"),
        str(Path.home()), str(ROOT),
    ) if value and len(value) >= 4]
    markers = [value.casefold().encode(encoding) for value in identities
               for encoding in ("utf-8", "utf-16le")]

    def scan(label, data):
        if any(marker in data.lower() for marker in markers):
            raise RuntimeError(f"Local identity found in release file: {label}")

    manifest = installer.validate_payload(payload)
    archive = CArchiveReader(str(setup))
    for relative, digest in manifest["files"].items():
        embedded = archive.extract(("payload/" + relative).replace("/", "\\"))
        assert hashlib.sha256(embedded).hexdigest() == digest, f"Embedded payload differs: {relative}"
    assert archive.extract("payload\\payload.json") == (payload / "payload.json").read_bytes()
    for path in payload.rglob("*"):
        if path.is_file():
            assert path.suffix.lower() != ".pdb", "Debug symbols must not ship"
            scan(path.relative_to(payload), path.read_bytes())
    for path in (setup, payload / "backend/LocalOpsWindowsBackend.exe",
                 payload / "backend/LocalOpsWindowsAnchor.exe"):
        archive = CArchiveReader(str(path))
        for name, record in archive.toc.items():
            if record[-1] == "z":
                pyz = archive.open_embedded_archive(name)
                for module in pyz.toc:
                    value = pyz.extract(module)
                    if value is not None:
                        scan(f"{path.name}:{module}", marshal.dumps(value))
            else:
                scan(f"{path.name}:{name}", archive.extract(name))
    print("PASS: embedded payload matches manifest; binary and decompressed-code privacy", flush=True)


def wait_for(check, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = check()
        if result:
            return result
        time.sleep(0.2)
    raise RuntimeError("Installer smoke check timed out")


def verify_backend(payload: Path, root: Path) -> None:
    port = None
    for candidate in range(9600, 9610):
        with socket.socket() as probe:
            try:
                probe.bind(("127.0.0.1", candidate))
                port = candidate
                break
            except OSError:
                pass
    if port is None:
        raise RuntimeError("No free backend test port; existing services were left untouched")
    environment = dict(os.environ, CONSOLE_DATA_DIR=str(root / "data"),
                       CONSOLE_LOG_DIR=str(root / "logs"),
                       PATH=str(Path(os.environ["SystemRoot"]) / "System32"))
    backend = payload / "backend/LocalOpsWindowsBackend.exe"
    process = subprocess.Popen(
        [str(backend), "--background", "--no-browser", "--preferred-port", str(port)],
        env=environment, cwd=backend.parent, creationflags=subprocess.CREATE_NO_WINDOW)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(path, body=None):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data,
                                     headers={"Content-Type": "application/json"})
        with opener.open(req, timeout=10) as response:
            return json.load(response)

    def health():
        if process.poll() is not None:
            raise RuntimeError(f"Packaged backend exited: {process.returncode}")
        try:
            value = request("/api/health")
            if value.get("consolePid") != process.pid:
                raise RuntimeError("Test port belongs to another process")
            return value
        except (OSError, urllib.error.URLError):
            return None

    try:
        assert wait_for(health)["platform"] == "windows"
        assert request("/api/state")["consolePid"] == process.pid
        fixture_ids = set()
        # Only OS commands are on PATH; the frozen anchor must run without Python.
        for code, status in ((0, "succeeded"), (130, "canceled"), (7, "failed")):
            app = request("/api/apps", {"name": "Release fixture", "kind": "task",
                                       "command": f"cmd.exe /d /c exit {code}",
                                       "cwd": str(root), "port": None})
            fixture_ids.add(app["id"])
            request(f"/api/apps/{app['id']}/start", {})
            def result():
                return next((item.get("lastExit") for item in request("/api/tasks/status")["apps"]
                             if item["id"] == app["id"]), None)
            outcome = wait_for(result)
            assert outcome["code"] == code and outcome["status"] == status, outcome
            log = request(f"/api/apps/{app['id']}/logs")["text"]
            assert log, "Frozen task did not produce lifecycle logs"
        assert request("/api/console/restart", {})["pid"] == process.pid
        assert process.wait(timeout=20) == 0

        def restarted():
            try:
                value = request("/api/health")
                return value if value.get("consolePid") != process.pid else None
            except (OSError, urllib.error.URLError):
                return None

        restarted_pid = int(wait_for(restarted)["consolePid"])
        identity = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
             f"(Get-Process -Id {restarted_pid} -ErrorAction Stop).MainModule.FileName"],
            capture_output=True, text=True, check=True, timeout=15,
            creationflags=subprocess.CREATE_NO_WINDOW)
        assert Path(identity.stdout.strip()).resolve() == backend.resolve(), "Unexpected restarted backend"
        assert fixture_ids == {item["id"] for item in request("/api/state")["apps"]}
        assert (root / "logs/console.log").stat().st_size > 0
        request("/api/console/stop", {})
        assert installer._wait_for_pid(restarted_pid, timeout=15)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait(timeout=10)
        # Also covers a restart helper that has not reached its health endpoint.
        installer._stop_installed_app()


def verify(payload: Path, setup: Path) -> None:
    verify_archive_privacy(payload, setup)
    check = subprocess.run([str(setup), "--verify-only", "--silent"],
                           timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
    assert check.returncode == 0, "Frozen setup rejected its embedded payload"
    (ROOT / "tmp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="installer-smoke-", dir=ROOT / "tmp") as directory:
        root = Path(directory)
        target = root / installer.PRODUCT_ID
        shortcuts = (root / "Start.lnk", root / "Desktop.lnk")
        registry = r"Software\LocalOpsWindows.Tests" + "\\" + uuid.uuid4().hex
        with ExitStack() as stack:
            for name, value in (("INSTALL_DIR", target), ("RUN_KEY", registry + r"\Run"),
                                ("UNINSTALL_KEY", registry + r"\Uninstall")):
                stack.enter_context(mock.patch.object(installer, name, value))
            stack.enter_context(mock.patch.object(installer, "_payload_dir", return_value=payload))
            stack.enter_context(mock.patch.object(installer, "_shortcut_paths", return_value=shortcuts))
            stack.enter_context(mock.patch.object(installer, "_remove_legacy_scheduled_task"))
            stack.enter_context(mock.patch.object(installer.sys, "frozen", True, create=True))
            stack.enter_context(mock.patch.object(installer.sys, "executable", str(setup)))
            try:
                installer.install(launch=False)
                print("PASS: isolated install and shortcuts", flush=True)
                installed = installer._current_executable().parent
                assert installer.validate_payload(installed)["version"]
                assert all(path.stat().st_size > 100 for path in shortcuts)
                assert (target / installer.SETUP_EXE).read_bytes() == setup.read_bytes()
                assert "--background" in installer._registry_snapshot(installer.RUN_KEY)[installer.PRODUCT_ID][0]
                verify_backend(installed, root)
                print("PASS: frozen backend, task exit codes, restart and stop", flush=True)
                config = (root / "data/config.json").read_bytes()
                installer._set_autostart(False)
                installer.install(launch=False)
                assert installer.PRODUCT_ID not in installer._registry_snapshot(installer.RUN_KEY)
                assert (root / "data/config.json").read_bytes() == config
                print("PASS: reinstall preserves data and autostart preference", flush=True)
                popen = subprocess.Popen
                with (root / "uninstall-helper.log").open("wb") as log:
                    def capture_helper(command, **kwargs):
                        if any("Remove-Item -LiteralPath" in arg for arg in command):
                            kwargs.update(stdout=log, stderr=log)
                        return popen(command, **kwargs)
                    with mock.patch.object(subprocess, "Popen", side_effect=capture_helper):
                        cleanup = installer.uninstall(silent=True)
                    print(f"Uninstall helper exit: {cleanup.wait(timeout=60)}", flush=True)
                if target.exists():
                    print((root / "uninstall-helper.log").read_text(errors="replace"), flush=True)
                wait_for(lambda: not target.exists())
                assert all(not path.exists() for path in shortcuts)
                assert (root / "data/config.json").read_bytes() == config
            finally:
                for key in (registry + r"\Run", registry + r"\Uninstall", registry):
                    try:
                        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, key)
                    except FileNotFoundError:
                        pass
    print("PASS: setup integrity, isolated install, real shortcuts, HKCU, frozen tasks, reinstall and uninstall")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--payload", type=Path, required=True)
    parser.add_argument("--setup", type=Path, required=True)
    args = parser.parse_args()
    verify(args.payload.resolve(), args.setup.resolve())
