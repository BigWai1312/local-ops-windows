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


def verify_archive_privacy(payload: Path, setup: Path, *, embedded_payload: bool = True) -> None:
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
    if embedded_payload:
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
    print("PASS: payload manifest, binary and decompressed-code privacy", flush=True)


def wait_for(check, timeout=30):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = check()
        if result:
            return result
        time.sleep(0.2)
    raise RuntimeError("Installer smoke check timed out")


def verify_backend(payload: Path, root: Path, migrated_app: dict | None = None, *,
                   expect_empty: bool = False) -> None:
    port = 0
    environment = dict(os.environ, PATH=str(Path(os.environ["SystemRoot"]) / "System32"))
    environment.pop("CONSOLE_DATA_DIR", None)
    environment.pop("CONSOLE_LOG_DIR", None)
    backend = payload / "backend/LocalOpsWindowsBackend.exe"
    process = subprocess.Popen(
        [str(backend), "--background", "--no-browser"],
        env=environment, cwd=backend.parent, creationflags=subprocess.CREATE_NO_WINDOW)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(path, body=None):
        data = None if body is None else json.dumps(body).encode()
        req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=data,
                                     headers={"Content-Type": "application/json"})
        with opener.open(req, timeout=10) as response:
            return json.load(response)

    def health():
        nonlocal port
        if process.poll() is not None:
            raise RuntimeError(f"Packaged backend exited: {process.returncode}")
        try:
            endpoint = json.loads((root / "data/backend-endpoint.json").read_text(encoding="utf-8"))
            if endpoint.get("consolePid") != process.pid:
                return None
            port = int(endpoint["port"])
            assert 1024 <= port <= 65535
            value = request("/api/health")
            if value.get("consolePid") != process.pid:
                raise RuntimeError("Test port belongs to another process")
            return value
        except (OSError, urllib.error.URLError, json.JSONDecodeError):
            return None

    try:
        initial_health = wait_for(health)
        assert initial_health["platform"] == "windows"
        assert Path(initial_health["dataDirectory"]).resolve() == root / "data"
        assert Path(initial_health["logsDirectory"]).resolve() == root / "logs"
        state = request("/api/state")
        assert state["consolePid"] == process.pid
        if expect_empty:
            assert state["apps"] == [], "First install imported unexpected projects"
        if migrated_app is not None:
            migrated = next(item for item in state["apps"] if item["id"] == migrated_app["id"])
            for key in ("name", "kind", "command", "cwd", "port"):
                assert migrated[key] == migrated_app[key], f"Upgrade changed project {key}"
        fixture_ids = {item["id"] for item in state["apps"]}
        # Only OS commands are on PATH; the frozen anchor must run without Python.
        for code, status in ((0, "succeeded"), (130, "canceled"), (7, "failed")):
            app = request("/api/apps", {"name": "Release fixture", "kind": "task",
                                       "command": f"cmd.exe /d /c \"chcp 65001 >nul & echo %TEMP% & exit {code}\"",
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
            assert str(root / "cache/temp") in log, f"Anchor temp directory mismatch: {log!r}"
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
             "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);"
             f"(Get-Process -Id {restarted_pid} -ErrorAction Stop).MainModule.FileName"],
            capture_output=True, text=True, encoding="utf-8", check=True, timeout=15,
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
        env=dict(os.environ, CONSOLE_DATA_DIR=str(Path(os.environ["APPDATA"]) / installer.PRODUCT_ID),
                 CONSOLE_LOG_DIR=str(Path(os.environ["LOCALAPPDATA"]) / installer.PRODUCT_ID / "logs")),
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
             "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);"
             f"Get-CimInstance Win32_Process -Filter 'ProcessId={pid}' | "
             "Select-Object ExecutablePath,ParentProcessId | ConvertTo-Json -Compress"],
            capture_output=True, text=True, encoding="utf-8", check=True, timeout=15,
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
    global installer
    from PyInstaller.archive.readers import CArchiveReader

    verify_archive_privacy(payload, setup)
    archive = CArchiveReader(str(setup))
    if f"python{sys.version_info.major}{sys.version_info.minor}.dll" not in archive.toc:
        raise RuntimeError("Installer verification requires the Python version used to build setup")
    embedded = types.ModuleType("verified_installer")
    embedded.__file__ = str(setup)
    exec(marshal.loads(archive.extract("installer")), embedded.__dict__)
    installer = embedded
    check = subprocess.run([str(setup), "--verify-only", "--silent"],
                           timeout=90, creationflags=subprocess.CREATE_NO_WINDOW)
    assert check.returncode == 0, "Frozen setup rejected its embedded payload"
    (ROOT / "tmp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="installer-smoke-", dir=ROOT / "tmp") as directory:
        root = Path(directory)
        target = root / "中文 空格" / installer.PRODUCT_ID
        target.parent.mkdir()
        shortcuts = (root / "Start.lnk", root / "Desktop.lnk")
        registry = r"Software\LocalOpsWindows.Tests" + "\\" + uuid.uuid4().hex
        with ExitStack() as stack:
            for name, value in (("INSTALL_DIR", target), ("RUN_KEY", registry + r"\Run"),
                                ("UNINSTALL_KEY", registry + r"\Uninstall"),
                                ("SETTINGS_KEY", registry + r"\Client")):
                stack.enter_context(mock.patch.object(installer, name, value))
            stack.enter_context(mock.patch.object(installer, "_payload_dir", return_value=payload))
            stack.enter_context(mock.patch.object(installer, "_shortcut_paths", return_value=shortcuts))
            stack.enter_context(mock.patch.object(installer, "_remove_legacy_scheduled_task"))
            stack.enter_context(mock.patch.object(installer.sys, "frozen", True, create=True))
            stack.enter_context(mock.patch.object(installer.sys, "executable", str(setup)))
            stack.enter_context(mock.patch.dict(os.environ, {
                "APPDATA": str(root / "roaming"), "LOCALAPPDATA": str(root / "local")}))
            try:
                migrated_app = None
                legacy_process = None
                if legacy_setup is not None:
                    legacy_process, migrated_app = prepare_legacy_install(
                        legacy_setup, root, shortcuts, registry)
                    old_data = root / "roaming" / installer.PRODUCT_ID / "config.json"
                    old_config = old_data.read_bytes()
                    old_shortcuts = {path: path.read_bytes() for path in shortcuts}
                    old_registry = installer._registry_snapshot(installer.UNINSTALL_KEY)
                    with mock.patch.object(installer, "_create_shortcut",
                                           side_effect=OSError("Upgrade rollback fixture")):
                        try:
                            installer.install(launch=False, migrate=True)
                        except OSError as error:
                            assert str(error) == "Upgrade rollback fixture"
                        else:
                            raise AssertionError("Upgrade failure was not reported")
                    assert not (target / installer.CURRENT_NAME).exists()
                    assert (target / installer.SETUP_EXE).read_bytes() == legacy_setup.read_bytes()
                    assert old_registry == installer._registry_snapshot(installer.UNINSTALL_KEY)
                    assert all(path.read_bytes() == content for path, content in old_shortcuts.items())
                    assert installer.PRODUCT_ID not in installer._registry_snapshot(installer.RUN_KEY)
                    assert old_data.read_bytes() == old_config
                    assert legacy_process.wait(timeout=15) is not None
                    print("PASS: failed legacy upgrade restores setup, shortcuts, registry and config", flush=True)
                installer.install(launch=False, migrate=legacy_setup is not None)
                print("PASS: isolated install and shortcuts", flush=True)
                installed = installer._current_executable().parent
                assert installer.validate_payload(installed)["version"]
                assert all(path.stat().st_size > 100 for path in shortcuts)
                assert (target / installer.SETUP_EXE).read_bytes() == setup.read_bytes()
                if legacy_setup is None:
                    assert "--background" in installer._registry_snapshot(installer.RUN_KEY)[installer.PRODUCT_ID][0]
                else:
                    assert installer.PRODUCT_ID not in installer._registry_snapshot(installer.RUN_KEY)
                    assert (target / "data/config.json").read_bytes() == old_config
                    for shortcut in shortcuts:
                        command = (
                            "[Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false);"
                            "$shell=New-Object -ComObject WScript.Shell;"
                            f"$shell.CreateShortcut({installer._ps_quote(shortcut)}).TargetPath"
                        )
                        result = subprocess.run(
                            ["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                            capture_output=True, text=True, encoding="utf-8", check=True, timeout=15,
                            creationflags=subprocess.CREATE_NO_WINDOW)
                        assert Path(result.stdout.strip()).resolve() == installer._current_executable().resolve()
                    print("PASS: legacy upgrade preserves config and disabled autostart, redirects shortcuts", flush=True)
                verify_backend(installed, target, migrated_app)
                if legacy_setup is None:
                    allowed_shell_directories = {"local/Microsoft", "local/Microsoft/Windows",
                                                 "local/Microsoft/Windows/Caches"}
                    unexpected = [str(p.relative_to(root)) for folder in (root / "roaming", root / "local")
                                  for p in folder.rglob("*") if not p.is_dir()
                                  or p.relative_to(root).as_posix() not in allowed_shell_directories]
                    assert not unexpected, f"Unexpected AppData entries: {unexpected}"
                else:
                    assert old_data.read_bytes() == old_config
                print("PASS: frozen backend, task exit codes, restart and stop", flush=True)
                config = (target / "data/config.json").read_bytes()
                installer._set_autostart(False)
                installer.install(launch=False)
                assert installer.PRODUCT_ID not in installer._registry_snapshot(installer.RUN_KEY)
                assert (target / "data/config.json").read_bytes() == config
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
                if (target / "versions").exists():
                    print((root / "uninstall-helper.log").read_text(errors="replace"), flush=True)
                wait_for(lambda: not any((target / name).exists()
                                         for name in ("versions", "cache", installer.SETUP_EXE, installer.CURRENT_NAME)))
                assert all(not path.exists() for path in shortcuts)
                assert (target / "data/config.json").read_bytes() == config
                assert (target / "logs/console.log").stat().st_size > 0
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
