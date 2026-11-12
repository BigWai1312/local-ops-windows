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
import types
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


def verify_backend(payload: Path, root: Path, migrated_app: dict | None = None) -> None:
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
        state = request("/api/state")
        assert state["consolePid"] == process.pid
        if migrated_app is not None:
            migrated = next(item for item in state["apps"] if item["id"] == migrated_app["id"])
            for key in ("name", "kind", "command", "cwd", "port"):
                assert migrated[key] == migrated_app[key], f"Upgrade changed project {key}"
        fixture_ids = {item["id"] for item in state["apps"]}
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


def prepare_legacy_install(setup: Path, root: Path, shortcuts: tuple[Path, Path],
                           registry: str) -> tuple[subprocess.Popen, dict]:
    from PyInstaller.archive.readers import CArchiveReader

    archive = CArchiveReader(str(setup))
    if "python312.dll" not in archive.toc or sys.version_info[:2] != (3, 12):
        raise RuntimeError("Legacy installer verification requires matching Python 3.12")
    payload = root / "legacy-payload"
    for name in archive.toc:
        if name.startswith("payload\\"):
            relative = Path(name.removeprefix("payload\\"))
            destination = payload / relative
            if not destination.resolve().is_relative_to(payload.resolve()):
                raise RuntimeError("Legacy payload path escapes test directory")
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(archive.extract(name))
    legacy = types.ModuleType("legacy_installer")
    legacy.__file__ = str(setup)
    exec(marshal.loads(archive.extract("installer")), legacy.__dict__)
    with ExitStack() as stack:
        for name, value in (("INSTALL_DIR", installer.INSTALL_DIR),
                            ("RUN_KEY", registry + r"\Run"),
                            ("UNINSTALL_KEY", registry + r"\Uninstall")):
            stack.enter_context(mock.patch.object(legacy, name, value))
        stack.enter_context(mock.patch.object(legacy, "_payload_dir", return_value=payload))
        stack.enter_context(mock.patch.object(legacy, "_shortcut_paths", return_value=shortcuts))
        stack.enter_context(mock.patch.object(legacy, "_remove_legacy_scheduled_task"))
        stack.enter_context(mock.patch.object(legacy, "_launch_main"))
        stack.enter_context(mock.patch.object(sys, "executable", str(setup)))
        legacy.install(silent=True)
        legacy._set_autostart(False)
    old_executable = installer.INSTALL_DIR / "LocalOpsWindows.exe"
    assert old_executable.read_bytes() == (payload / old_executable.name).read_bytes()
    assert (installer.INSTALL_DIR / installer.SETUP_EXE).read_bytes() == setup.read_bytes()
    assert installer._current_executable() == old_executable
    assert all(path.stat().st_size > 100 for path in shortcuts)
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
        raise RuntimeError("No free legacy test port")
    process = subprocess.Popen(
        [str(old_executable), "--no-browser", "--background", "--preferred-port", str(port)],
        cwd=old_executable.parent,
        env=dict(os.environ, CONSOLE_DATA_DIR=str(root / "data"),
                 CONSOLE_LOG_DIR=str(root / "logs")),
        creationflags=subprocess.CREATE_NO_WINDOW)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def ready():
        if process.poll() is not None:
            raise RuntimeError(f"Legacy backend exited: {process.returncode}")
        try:
            with opener.open(f"http://127.0.0.1:{port}/api/state", timeout=5) as response:
                state = json.load(response)
        except (OSError, urllib.error.URLError):
            return None
        pid = int(state["consolePid"])
        identity = subprocess.run(
            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command",
             f"Get-CimInstance Win32_Process -Filter 'ProcessId={pid}' | "
             "Select-Object ExecutablePath,ParentProcessId | ConvertTo-Json -Compress"],
            capture_output=True, text=True, check=True, timeout=15,
            creationflags=subprocess.CREATE_NO_WINDOW)
        owner = json.loads(identity.stdout)
        assert Path(owner["ExecutablePath"]).resolve() == old_executable.resolve()
        # Onefile packages serve from a child of the bootloader process.
        assert pid == process.pid or owner["ParentProcessId"] == process.pid
        return state

    wait_for(ready)
    request = urllib.request.Request(
        f"http://127.0.0.1:{port}/api/apps",
        data=json.dumps({"name": "Legacy upgrade fixture", "kind": "task",
                         "command": "cmd.exe /d /c exit 0", "cwd": str(root),
                         "port": None}).encode(),
        headers={"Content-Type": "application/json"})
    with opener.open(request, timeout=10) as response:
        app = json.load(response)
    print("PASS: real legacy installer code, binary startup and saved project", flush=True)
    return process, app


def verify(payload: Path, setup: Path, legacy_setup: Path | None = None) -> None:
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
                migrated_app = None
                legacy_process = None
                if legacy_setup is not None:
                    legacy_process, migrated_app = prepare_legacy_install(
                        legacy_setup, root, shortcuts, registry)
                    old_config = (root / "data/config.json").read_bytes()
                    old_shortcuts = {path: path.read_bytes() for path in shortcuts}
                    old_registry = installer._registry_snapshot(installer.UNINSTALL_KEY)
                    with mock.patch.object(installer, "_create_shortcut",
                                           side_effect=OSError("Upgrade rollback fixture")):
                        try:
                            installer.install(launch=False)
                        except OSError as error:
                            assert str(error) == "Upgrade rollback fixture"
                        else:
                            raise AssertionError("Upgrade failure was not reported")
                    assert not (target / installer.CURRENT_NAME).exists()
                    assert (target / installer.SETUP_EXE).read_bytes() == legacy_setup.read_bytes()
                    assert old_registry == installer._registry_snapshot(installer.UNINSTALL_KEY)
                    assert all(path.read_bytes() == content for path, content in old_shortcuts.items())
                    assert installer.PRODUCT_ID not in installer._registry_snapshot(installer.RUN_KEY)
                    assert (root / "data/config.json").read_bytes() == old_config
                    assert legacy_process.wait(timeout=15) is not None
                    print("PASS: failed legacy upgrade restores setup, shortcuts, registry and config", flush=True)
                installer.install(launch=False)
                print("PASS: isolated install and shortcuts", flush=True)
                installed = installer._current_executable().parent
                assert installer.validate_payload(installed)["version"]
                assert all(path.stat().st_size > 100 for path in shortcuts)
                assert (target / installer.SETUP_EXE).read_bytes() == setup.read_bytes()
                if legacy_setup is None:
                    assert "--background" in installer._registry_snapshot(installer.RUN_KEY)[installer.PRODUCT_ID][0]
                else:
                    assert installer.PRODUCT_ID not in installer._registry_snapshot(installer.RUN_KEY)
                    assert (root / "data/config.json").read_bytes() == old_config
                    for shortcut in shortcuts:
                        command = (
                            "$shell=New-Object -ComObject WScript.Shell;"
                            f"$shell.CreateShortcut({installer._ps_quote(shortcut)}).TargetPath"
                        )
                        result = subprocess.run(
                            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                            capture_output=True, text=True, check=True, timeout=15,
                            creationflags=subprocess.CREATE_NO_WINDOW)
                        assert Path(result.stdout.strip()).resolve() == installer._current_executable().resolve()
                    print("PASS: legacy upgrade preserves config and disabled autostart, redirects shortcuts", flush=True)
                verify_backend(installed, root, migrated_app)
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
                installer._stop_installed_app()
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
    parser.add_argument("--legacy-setup", type=Path,
                        help="Trusted local Python 3.12 installer to verify a real legacy upgrade")
    args = parser.parse_args()
    verify(args.payload.resolve(), args.setup.resolve(),
           args.legacy_setup.resolve() if args.legacy_setup else None)
