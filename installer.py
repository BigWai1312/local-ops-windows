#!/usr/bin/env python3
"""Per-user installer, updater and uninstaller for Local Ops Windows.

The frozen setup executable carries a hashed versioned C# and backend
``payload`` directory. It deliberately installs under ``%LOCALAPPDATA%`` so
normal users do not need elevation and existing Docker projects are untouched.
"""

from __future__ import annotations

import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import re
import subprocess
import sys
import time
import tempfile
import winreg


PRODUCT_ID = "LocalOpsWindows"
DISPLAY_NAME = "Local Ops Windows"
MAIN_EXE = "LocalOpsWindows.Client.exe"
ANCHOR_EXE = "LocalOpsWindowsAnchor.exe"
SETUP_EXE = "LocalOpsWindows-Setup.exe"
NATIVE_APP_DIR = "app"
PAYLOAD_DOCUMENTS = (
    "LICENSE",
    "NOTICE.md",
    "THIRD_PARTY_NOTICES.md",
)
INSTALL_DIR = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "Programs" / PRODUCT_ID
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall" + "\\" + PRODUCT_ID
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
LEGACY_TASK_NAME = "Local Ops Windows"
MANIFEST_NAME = "payload.json"
CURRENT_NAME = "current.json"
STABLE_VERSION_RE = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")


def _hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def validate_payload(payload: Path) -> dict:
    manifest = json.loads((payload / MANIFEST_NAME).read_text(encoding="utf-8"))
    if (manifest.get("product") != "LocalOpsWindows.CSharp"
            or not STABLE_VERSION_RE.fullmatch(str(manifest.get("version", "")))):
        raise ValueError("安装包产品或版本无效")
    files = manifest.get("files")
    if not isinstance(files, dict) or not 1 <= len(files) <= 5000:
        raise ValueError("安装清单无效")
    required = {MAIN_EXE, "backend/LocalOpsWindowsBackend.exe",
                "backend/" + ANCHOR_EXE, "LICENSE", "NOTICE.md",
                "THIRD_PARTY_NOTICES.md", "Assets/local-ops.ico", "resources.json",
                "LocalOpsWindows.Client.dll", "LocalOpsWindows.Client.runtimeconfig.json",
                "LocalOpsWindows.Client.deps.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"}
    if not required.issubset(files):
        raise ValueError("安装包缺少客户端、后台或许可证")
    total = 0
    for relative, digest in files.items():
        if (not isinstance(relative, str)
                or "\\" in relative or ":" in relative
                or any(part in {"", ".", ".."} for part in relative.split("/"))
                or not re.fullmatch(r"[A-Za-z0-9_. /+-]+", relative)
                or not re.fullmatch(r"[0-9a-f]{64}", str(digest))):
            raise ValueError("安装清单路径或摘要无效")
        path = payload / relative
        if not path.resolve().is_relative_to(payload.resolve()) or path.is_symlink():
            raise ValueError("安装清单路径越界")
        total += path.stat().st_size
        if total > 2 * 1024 ** 3 or _hash(path) != digest:
            raise ValueError("安装包完整性校验失败")
    actual = {path.relative_to(payload).as_posix() for path in payload.rglob("*") if path.is_file()}
    if actual != set(files) | {MANIFEST_NAME}:
        raise ValueError("安装包存在未登记文件")
    return manifest


def _current_executable() -> Path:
    try:
        value = json.loads((INSTALL_DIR / CURRENT_NAME).read_text(encoding="utf-8"))
        relative = value["directory"]
        if not re.fullmatch(r"versions/\d+\.\d+\.\d+-[0-9a-f]{12}", relative):
            raise ValueError("版本目录无效")
        target = (INSTALL_DIR / relative / MAIN_EXE).resolve()
        if not target.is_relative_to(INSTALL_DIR.resolve()):
            raise ValueError("版本目录越界")
        return target
    except FileNotFoundError:
        return INSTALL_DIR / "LocalOpsWindows.exe"


def _payload_dir() -> Path:
    root = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
    return root / "payload"


def _shortcut_paths() -> tuple[Path, Path]:
    appdata = Path(os.environ.get("APPDATA") or Path.home())
    start_menu = appdata / "Microsoft" / "Windows" / "Start Menu" / "Programs"
    return start_menu / f"{DISPLAY_NAME}.lnk", Path.home() / "Desktop" / f"{DISPLAY_NAME}.lnk"


def _pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = ctypes.c_void_p
    kernel32.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    kernel32.GetExitCodeProcess.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
    kernel32.CloseHandle.argtypes = [ctypes.c_void_p]
    handle = kernel32.OpenProcess(0x1000, False, pid)
    if not handle:
        return False
    try:
        code = ctypes.c_ulong()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return False
        return code.value == 259  # STILL_ACTIVE
    finally:
        kernel32.CloseHandle(handle)


def _wait_for_pid(pid: int, timeout: float = 20.0) -> bool:
    deadline = time.monotonic() + timeout
    while _pid_alive(pid) and time.monotonic() < deadline:
        time.sleep(0.1)
    return not _pid_alive(pid)


def _ps_quote(value: Path | str) -> str:
    return "'" + str(value).replace("'", "''") + "'"


def _create_shortcut(path: Path, target: Path, arguments: str = "") -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    script = (
        "$ws=New-Object -ComObject WScript.Shell;"
        f"$s=$ws.CreateShortcut({_ps_quote(path)});"
        f"$s.TargetPath={_ps_quote(target)};"
        f"$s.WorkingDirectory={_ps_quote(target.parent)};"
        f"$s.Arguments={_ps_quote(arguments)};"
        f"$s.IconLocation={_ps_quote(str(target) + ',0')};"
        "$s.Save()"
    )
    completed = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
        capture_output=True, text=True, timeout=20, creationflags=CREATE_NO_WINDOW)
    if completed.returncode:
        raise RuntimeError("创建快捷方式失败")


def _set_autostart(enabled: bool) -> None:
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
        if enabled:
            winreg.SetValueEx(key, PRODUCT_ID, 0, winreg.REG_SZ,
                              f'"{_current_executable()}" --background')
        else:
            try:
                winreg.DeleteValue(key, PRODUCT_ID)
            except FileNotFoundError:
                pass


def _write_uninstall_entry() -> None:
    with winreg.CreateKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY) as key:
        values = {
            "DisplayName": DISPLAY_NAME,
            "Publisher": "BigWai1312",
            "InstallLocation": str(INSTALL_DIR),
            "DisplayIcon": str(_current_executable()),
            "UninstallString": f'"{INSTALL_DIR / SETUP_EXE}" --uninstall',
            "QuietUninstallString": f'"{INSTALL_DIR / SETUP_EXE}" --uninstall --silent',
            "NoModify": 1,
            "NoRepair": 1,
        }
        for name, value in values.items():
            kind = winreg.REG_DWORD if isinstance(value, int) else winreg.REG_SZ
            winreg.SetValueEx(key, name, 0, kind, value)


def _remove_legacy_scheduled_task() -> None:
    """Remove the source-install login task replaced by the HKCU Run entry."""
    subprocess.run(
        ["schtasks.exe", "/Delete", "/TN", LEGACY_TASK_NAME, "/F"],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        timeout=20, creationflags=CREATE_NO_WINDOW)


def _delete_uninstall_entry() -> None:
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY)
    except FileNotFoundError:
        pass


def _launch_main(background: bool) -> None:
    flags = CREATE_NO_WINDOW if background else 0
    subprocess.Popen(
        [str(_current_executable())] + (["--background"] if background else []),
        cwd=str(_current_executable().parent), stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL if background else None,
        stderr=subprocess.DEVNULL if background else None,
        creationflags=flags, close_fds=True)


def _stop_installed_app() -> None:
    """Leave anchors and projects alive; stop only exact installed client/backend paths."""
    targets = [
        path for path in (
            INSTALL_DIR / "LocalOpsWindows.exe",
            INSTALL_DIR / NATIVE_APP_DIR / "LocalOpsWindows.exe",
            _current_executable(),
            *list((INSTALL_DIR / "versions").glob("*/" + MAIN_EXE)),
            *list((INSTALL_DIR / "versions").glob("*/backend/LocalOpsWindowsBackend.exe")),
        ) if path.exists()
    ]
    targets = list(dict.fromkeys(path.resolve() for path in targets))
    if any(not path.is_relative_to(INSTALL_DIR.resolve()) for path in targets):
        raise RuntimeError("拒绝停止安装目录外的程序")
    if not targets:
        return
    target_literals = ",".join(_ps_quote(path) for path in targets)
    script = (
        "$ErrorActionPreference='Stop';"
        f"$targets=@({target_literals});"
        "Get-CimInstance Win32_Process -Filter \"Name='LocalOpsWindows.exe' OR Name='LocalOpsWindows.Client.exe' OR Name='LocalOpsWindowsBackend.exe'\" "
        "| Where-Object { $targets -contains $_.ExecutablePath } "
        "| ForEach-Object {"
        "try { $p=[Diagnostics.Process]::GetProcessById($_.ProcessId);"
        "$null=$p.Handle;"
        "if (($targets -contains $p.MainModule.FileName) -and "
        "([Math]::Abs(($p.StartTime.ToUniversalTime()-$_.CreationDate.ToUniversalTime()).TotalMilliseconds) -lt 2)) {"
        "$null=$p.CloseMainWindow();"
        "if (-not $p.WaitForExit(5000)) { $p.Kill(); $null=$p.WaitForExit(5000) }"
        "}; $p.Dispose() } catch [ArgumentException] {} }"
    )
    completed = subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
        capture_output=True, text=True, timeout=20, creationflags=CREATE_NO_WINDOW)
    if completed.returncode:
        raise RuntimeError("无法停止旧版本客户端或后台，请先退出后重试")
    deadline = time.monotonic() + 5.0
    while time.monotonic() < deadline:
        unlocked = True
        for target in targets:
            try:
                with target.open("rb+"):
                    pass
            except OSError:
                unlocked = False
                break
        if unlocked:
            return
        time.sleep(0.1)
    raise RuntimeError("旧版本文件仍在使用，安装已取消")


def _schedule_remove(path: Path) -> subprocess.Popen:
    if path.resolve() != INSTALL_DIR.resolve() or path.name != PRODUCT_ID:
        raise RuntimeError("拒绝删除产品安装目录之外的路径")
    script = f"Start-Sleep -Milliseconds 700; Remove-Item -LiteralPath {_ps_quote(path)} -Recurse -Force -ErrorAction SilentlyContinue"
    return subprocess.Popen(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=CREATE_NO_WINDOW, close_fds=True)


def _registry_snapshot(path: str) -> dict:
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, path) as key:
            values = {}
            for index in range(winreg.QueryInfoKey(key)[1]):
                name, value, kind = winreg.EnumValue(key, index)
                values[name] = (value, kind)
            return values
    except FileNotFoundError:
        return {}


def install(update_pid: int | None = None, silent: bool = False,
            launch: bool = True) -> None:
    payload = _payload_dir()
    manifest = validate_payload(payload)
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    version_dir = INSTALL_DIR / "versions" / (manifest["version"] + "-" + _hash(payload / MANIFEST_NAME)[:12])
    version_dir.parent.mkdir(parents=True, exist_ok=True)
    if not version_dir.exists():
        with tempfile.TemporaryDirectory(prefix=".install-", dir=INSTALL_DIR) as stage_name:
            stage = Path(stage_name) / "payload"
            shutil.copytree(payload, stage)
            validate_payload(stage)
            os.replace(stage, version_dir)
    else:
        validate_payload(version_dir)
    if update_pid is not None and not _wait_for_pid(update_pid):
        raise RuntimeError("旧版本进程未能退出，安装已取消")
    _stop_installed_app()
    state_files = [INSTALL_DIR / CURRENT_NAME, INSTALL_DIR / SETUP_EXE, *_shortcut_paths()]
    previous = {path: path.read_bytes() if path.exists() else None for path in state_files}
    run = _registry_snapshot(RUN_KEY).get(PRODUCT_ID)
    uninstall = _registry_snapshot(UNINSTALL_KEY)
    first_install = previous[INSTALL_DIR / CURRENT_NAME] is None and not uninstall
    try:
        current_temp = INSTALL_DIR / (CURRENT_NAME + ".new")
        current_temp.write_text(json.dumps({
            "version": manifest["version"],
            "directory": version_dir.relative_to(INSTALL_DIR).as_posix(),
        }), encoding="utf-8")
        os.replace(current_temp, INSTALL_DIR / CURRENT_NAME)
        setup_source = Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve()
        setup_destination = (INSTALL_DIR / SETUP_EXE).resolve()
        if setup_source != setup_destination:
            temporary = setup_destination.with_suffix(".new")
            shutil.copy2(setup_source, temporary)
            os.replace(temporary, setup_destination)
        _set_autostart(first_install or run is not None)
        _write_uninstall_entry()
        for shortcut in _shortcut_paths():
            _create_shortcut(shortcut, _current_executable())
    except Exception:
        for path, content in previous.items():
            if content is None:
                path.unlink(missing_ok=True)
            else:
                temporary = path.with_suffix(path.suffix + ".rollback")
                temporary.write_bytes(content)
                os.replace(temporary, path)
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, RUN_KEY) as key:
            if run is None:
                try:
                    winreg.DeleteValue(key, PRODUCT_ID)
                except FileNotFoundError:
                    pass
            else:
                winreg.SetValueEx(key, PRODUCT_ID, 0, run[1], run[0])
        _delete_uninstall_entry()
        if uninstall:
            with winreg.CreateKey(winreg.HKEY_CURRENT_USER, UNINSTALL_KEY) as key:
                for name, (value, kind) in uninstall.items():
                    winreg.SetValueEx(key, name, 0, kind, value)
        raise
    _remove_legacy_scheduled_task()
    if launch:
        _launch_main(background=silent)


def uninstall(silent: bool = False) -> subprocess.Popen:
    _stop_installed_app()
    _set_autostart(False)
    _delete_uninstall_entry()
    for shortcut in _shortcut_paths():
        try:
            shortcut.unlink()
        except FileNotFoundError:
            pass
    return _schedule_remove(INSTALL_DIR)


def _message_box(message: str) -> None:
    if getattr(sys, "frozen", False):
        ctypes.windll.user32.MessageBoxW(0, message, DISPLAY_NAME, 0x10)


def main() -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--uninstall", action="store_true")
    parser.add_argument("--update-pid", type=int)
    parser.add_argument("--silent", action="store_true")
    parser.add_argument("--no-launch", action="store_true")
    parser.add_argument("--verify-only", action="store_true")
    args, _ = parser.parse_known_args()
    try:
        if args.verify_only:
            validate_payload(_payload_dir())
        elif args.uninstall:
            uninstall(args.silent)
        else:
            install(args.update_pid, args.silent, launch=not args.no_launch)
        return 0
    except Exception as exc:
        if not args.silent:
            _message_box(str(exc))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
