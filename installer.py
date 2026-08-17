#!/usr/bin/env python3
"""Per-user installer, updater and uninstaller for Local Ops Windows.

The frozen setup executable carries the two application binaries in a
``payload`` directory. It deliberately installs under ``%LOCALAPPDATA%`` so
normal users do not need elevation and existing Docker projects are untouched.
"""

from __future__ import annotations

import argparse
import ctypes
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time
import winreg


PRODUCT_ID = "LocalOpsWindows"
DISPLAY_NAME = "Local Ops Windows"
MAIN_EXE = "LocalOpsWindows.exe"
ANCHOR_EXE = "LocalOpsWindowsAnchor.exe"
SETUP_EXE = "LocalOpsWindows-Setup.exe"
INSTALL_DIR = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / "Programs" / PRODUCT_ID
RUN_KEY = r"Software\Microsoft\Windows\CurrentVersion\Run"
UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall" + "\\" + PRODUCT_ID
CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
DETACHED_PROCESS = getattr(subprocess, "DETACHED_PROCESS", 0)
LEGACY_TASK_NAME = "Local Ops Windows"


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
                              f'"{INSTALL_DIR / MAIN_EXE}" --background')
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
            "DisplayIcon": str(INSTALL_DIR / MAIN_EXE),
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
        [str(INSTALL_DIR / MAIN_EXE)] + (["--background"] if background else []),
        cwd=str(INSTALL_DIR), stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL if background else None,
        stderr=subprocess.DEVNULL if background else None,
        creationflags=flags, close_fds=True)


def _stop_installed_app() -> None:
    """Stop only the installed executable at our exact per-user path."""
    target = INSTALL_DIR / MAIN_EXE
    if not target.exists():
        return
    script = (
        f"$target={_ps_quote(target)};"
        "Get-CimInstance Win32_Process -Filter \"Name='LocalOpsWindows.exe'\" "
        "| Where-Object { $_.ExecutablePath -eq $target } "
        "| ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }"
    )
    subprocess.run(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
        capture_output=True, text=True, timeout=20, creationflags=CREATE_NO_WINDOW)
    deadline = time.monotonic() + 5.0
    while time.monotonic() < deadline:
        try:
            with target.open("rb+"):
                return
        except OSError:
            time.sleep(0.1)


def _schedule_remove(path: Path) -> None:
    script = f"Start-Sleep -Milliseconds 700; Remove-Item -LiteralPath {_ps_quote(path)} -Recurse -Force -ErrorAction SilentlyContinue"
    subprocess.Popen(
        ["powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script],
        stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        creationflags=DETACHED_PROCESS | CREATE_NO_WINDOW, close_fds=True)


def install(update_pid: int | None = None, silent: bool = False) -> None:
    if update_pid is not None and not _wait_for_pid(update_pid):
        raise RuntimeError("旧版本进程未能退出，安装已取消")
    if update_pid is None:
        _stop_installed_app()
    payload = _payload_dir()
    for name in (MAIN_EXE, ANCHOR_EXE):
        if not (payload / name).is_file():
            raise RuntimeError(f"安装包缺少 {name}")
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    for name in (MAIN_EXE, ANCHOR_EXE):
        temporary = INSTALL_DIR / (name + ".new")
        shutil.copy2(payload / name, temporary)
        os.replace(temporary, INSTALL_DIR / name)
    setup_source = Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve()
    setup_destination = (INSTALL_DIR / SETUP_EXE).resolve()
    if setup_source.is_file() and setup_source != setup_destination:
        shutil.copy2(setup_source, INSTALL_DIR / SETUP_EXE)
    _set_autostart(True)
    _remove_legacy_scheduled_task()
    _write_uninstall_entry()
    start_menu, desktop = _shortcut_paths()
    _create_shortcut(start_menu, INSTALL_DIR / MAIN_EXE)
    _create_shortcut(desktop, INSTALL_DIR / MAIN_EXE)
    _launch_main(background=silent)


def uninstall(silent: bool = False) -> None:
    _stop_installed_app()
    _set_autostart(False)
    _delete_uninstall_entry()
    for shortcut in _shortcut_paths():
        try:
            shortcut.unlink()
        except FileNotFoundError:
            pass
    _schedule_remove(INSTALL_DIR)


def _message_box(message: str) -> None:
    if getattr(sys, "frozen", False):
        ctypes.windll.user32.MessageBoxW(0, message, DISPLAY_NAME, 0x10)


def main() -> int:
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--uninstall", action="store_true")
    parser.add_argument("--update-pid", type=int)
    parser.add_argument("--silent", action="store_true")
    args, _ = parser.parse_known_args()
    try:
        if args.uninstall:
            uninstall(args.silent)
        else:
            install(args.update_pid, args.silent)
        return 0
    except Exception as exc:
        if not args.silent:
            _message_box(str(exc))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
