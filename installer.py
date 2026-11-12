#!/usr/bin/env python3
"""Per-user installer, updater and uninstaller for Local Ops Windows.

Inno Setup owns the standard wizard, files, shortcuts and uninstaller.
The maintenance helper validates paths and preserves installed user state.
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
MARKER_NAME = "installation.json"
SETTINGS_KEY = r"Software\LocalOpsWindows\Client"
NATIVE_UNINSTALL_KEY = UNINSTALL_KEY + "_is1"
MAINTENANCE_EXE = "LocalOpsWindows.Maintenance.exe"

# Test distributions get separate registry identities baked into the binary.
_identity_path = Path(getattr(sys, "_MEIPASS", Path(__file__).parent)) / "setup-identity.json"
TEST_IDENTITY = None
if getattr(sys, "frozen", False) and _identity_path.is_file():
    TEST_IDENTITY = json.loads(_identity_path.read_text(encoding="utf-8")).get("testId")
    if not re.fullmatch(r"[0-9a-f]{32}", str(TEST_IDENTITY)):
        raise RuntimeError("Invalid installer test identity")
    UNINSTALL_KEY = r"Software\LocalOpsWindows.Tests" + "\\" + TEST_IDENTITY + r"\LegacyUninstall"
    SETTINGS_KEY = r"Software\LocalOpsWindows.Tests" + "\\" + TEST_IDENTITY + r"\Client"
    NATIVE_UNINSTALL_KEY = ("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\"
                            + "LocalOpsWindows.Test." + TEST_IDENTITY + "_is1")
STABLE_VERSION_RE = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")


def _hash(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def _assert_plain_tree(path: Path) -> None:
    """Do not follow directory junctions or other reparse points during copy/delete."""
    pending = [path]
    while pending:
        item = pending.pop()
        if not os.path.lexists(item):
            continue
        if item.lstat().st_file_attributes & 0x400:
            raise ValueError("目录包含链接或重解析点，操作已取消")
        if item.is_dir():
            pending.extend(item.iterdir())


def validate_install_dir(path: Path) -> Path:
    if not path.is_absolute():
        raise ValueError("请选择绝对路径")
    resolved = path.resolve()
    forbidden = {Path(path.anchor).resolve(), Path.home().resolve()}
    for name in ("SystemRoot", "ProgramFiles", "ProgramFiles(x86)", "APPDATA", "LOCALAPPDATA"):
        if os.environ.get(name):
            forbidden.add(Path(os.environ[name]).resolve())
    if resolved in forbidden or any(parent.is_symlink() or parent.is_junction()
                                     for parent in (path, *path.parents)):
        raise ValueError("请选择独立的程序目录，不能使用系统目录、用户根目录或目录链接")
    if resolved.exists() and any(resolved.iterdir()):
        marker = resolved / MARKER_NAME
        registered = installed_root()
        if marker.exists():
            if json.loads(marker.read_text(encoding="utf-8")).get("product") != PRODUCT_ID:
                raise ValueError("目录不属于 Local Ops Windows")
        elif not (registered and registered.resolve() == resolved
                  and (resolved / SETUP_EXE).is_file()):
            raise ValueError("目录非空，请选择空目录或本程序已有的安装目录")
    for name in ("versions", "cache", "data", "logs", SETUP_EXE, CURRENT_NAME, MARKER_NAME):
        _assert_plain_tree(resolved / name)
    return resolved


def installed_root() -> Path | None:
    value = (_registry_snapshot(NATIVE_UNINSTALL_KEY).get("InstallLocation", (None, 0))[0]
             or _registry_snapshot(UNINSTALL_KEY).get("InstallLocation", (None, 0))[0])
    return Path(value) if value else None


def prepare_native_install(update_pid: int | None = None) -> None:
    validate_install_dir(INSTALL_DIR)
    previous_root = installed_root()
    if _registry_snapshot(NATIVE_UNINSTALL_KEY) and previous_root and previous_root.resolve() != INSTALL_DIR.resolve():
        raise RuntimeError("升级请沿用已有安装目录；更换位置前请先卸载，项目数据不会删除")
    if update_pid is not None and not _wait_for_pid(update_pid):
        raise RuntimeError("旧版本进程未能退出，安装已取消")
    _stop_installed_app()
    if previous_root and previous_root.resolve() != INSTALL_DIR.resolve():
        _stop_installed_app(previous_root)
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryFile(dir=INSTALL_DIR) as probe:
        probe.write(b"write-test")
    marker = INSTALL_DIR / MARKER_NAME
    if not marker.exists():
        marker.write_text(json.dumps({"product": PRODUCT_ID, "storageVersion": 1}), encoding="utf-8")


def prepare_native_uninstall() -> None:
    _removal_targets(INSTALL_DIR)
    _assert_plain_tree(INSTALL_DIR)
    _stop_installed_app()
    # Inno removes its registered files, never an entire user-selected root.
    cache = INSTALL_DIR / "cache"
    if cache.exists():
        shutil.rmtree(cache)


def finalize_native_install(version_directory: str) -> None:
    if not re.fullmatch(r"\d+\.\d+\.\d+-[0-9a-f]{12}", version_directory):
        raise RuntimeError("版本目录无效")
    payload = INSTALL_DIR / "versions" / version_directory
    _assert_plain_tree(payload)
    manifest = validate_payload(payload)
    native = _registry_snapshot(NATIVE_UNINSTALL_KEY).get("InstallLocation", (None, 0))[0]
    if not native or Path(native).resolve() != INSTALL_DIR.resolve():
        raise RuntimeError("标准安装器尚未注册安装位置")
    temporary = INSTALL_DIR / (CURRENT_NAME + ".new")
    temporary.write_text(json.dumps({"version": manifest["version"],
                                     "directory": "versions/" + version_directory}), encoding="utf-8")
    os.replace(temporary, INSTALL_DIR / CURRENT_NAME)


def migrate_user_data(source: Path | None) -> None:
    if (INSTALL_DIR / "data" / "config.json").exists():
        return
    legacy = Path(os.environ.get("APPDATA") or Path.home()) / PRODUCT_ID
    local = Path(os.environ.get("LOCALAPPDATA") or Path.home()) / PRODUCT_ID
    data = source / "data" if source and (source / "data/config.json").exists() else legacy
    logs = source / "logs" if source and (source / "logs").exists() else local / "logs"
    _assert_plain_tree(data)
    _assert_plain_tree(logs)
    preferences = {name: value for name, (value, _) in _registry_snapshot(SETTINGS_KEY).items()
                   if name in {"Theme", "WindowBounds", "TaskNotifications", "MinimizeToTray"}
                   and isinstance(value, str)}
    if not data.exists() and not preferences:
        return
    if (data / "config.json").is_file():
        json.loads((data / "config.json").read_text(encoding="utf-8"))
    # Preserve originals and copy only durable state, never the instance lock.
    with tempfile.TemporaryDirectory(prefix=".migration-", dir=INSTALL_DIR) as folder:
        stage = Path(folder) / "data"
        stage.mkdir()
        for name in ("config.json", "config.json.bak", "client-settings.json"):
            origin = data / name
            if origin.is_file():
                shutil.copy2(origin, stage / name)
                if _hash(origin) != _hash(stage / name):
                    raise RuntimeError("迁移时配置发生变化，请退出旧客户端后重试")
        if (data / "icons").exists():
            shutil.copytree(data / "icons", stage / "icons")
        if preferences and not (stage / "client-settings.json").exists():
            (stage / "client-settings.json").write_text(json.dumps(preferences), encoding="utf-8")
        staged_logs = Path(folder) / "logs"
        if logs.is_dir() and not (INSTALL_DIR / "logs").exists():
            shutil.copytree(logs, staged_logs)
        destination = INSTALL_DIR / "data"
        if destination.exists():
            if any(destination.iterdir()):
                raise RuntimeError("目标数据目录非空，拒绝覆盖")
            destination.rmdir()
        os.replace(stage, destination)
        if staged_logs.exists():
            os.replace(staged_logs, INSTALL_DIR / "logs")


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


def _stop_installed_app(root: Path | None = None) -> None:
    """Leave anchors and projects alive; stop only exact installed client/backend paths."""
    root = root or INSTALL_DIR
    targets = [
        path for path in (
            root / "LocalOpsWindows.exe",
            root / NATIVE_APP_DIR / "LocalOpsWindows.exe",
            *list((root / "versions").glob("*/" + MAIN_EXE)),
            *list((root / "versions").glob("*/backend/LocalOpsWindowsBackend.exe")),
        ) if path.exists()
    ]
    targets = list(dict.fromkeys(path.resolve() for path in targets))
    if any(not path.is_relative_to(root.resolve()) for path in targets):
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


def _removal_targets(path: Path) -> list[Path]:
    marker = path / MARKER_NAME
    if (path.resolve() != INSTALL_DIR.resolve() or not marker.is_file()
            or json.loads(marker.read_text(encoding="utf-8")).get("product") != PRODUCT_ID):
        raise RuntimeError("拒绝删除产品安装目录之外的路径")
    validate_install_dir(path)
    registered = installed_root()
    if registered and registered.resolve() != path.resolve():
        raise RuntimeError("此目录不是当前注册的安装位置，拒绝卸载其他位置的程序")
    # Remove only program-owned paths; data, logs, and unknown user files remain.
    targets = [path / name for name in ("versions", "cache", SETUP_EXE, CURRENT_NAME,
                                       "LocalOpsWindows.exe", ANCHOR_EXE, NATIVE_APP_DIR)]
    if any(target.is_symlink() or target.is_junction()
           or not target.resolve().is_relative_to(path.resolve()) for target in targets):
        raise RuntimeError("拒绝删除指向安装目录外的路径")
    for target in targets:
        _assert_plain_tree(target)
    return targets


def _schedule_remove(path: Path) -> subprocess.Popen:
    targets = _removal_targets(path)
    literals = ",".join(_ps_quote(target) for target in targets)
    script = ("Start-Sleep -Milliseconds 1000;"
              f"foreach($target in @({literals})) {{"
              "if(Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop } }")
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
            launch: bool = True, migrate: bool = False) -> None:
    payload = _payload_dir()
    manifest = validate_payload(payload)
    validate_install_dir(INSTALL_DIR)
    previous_root = installed_root()
    INSTALL_DIR.mkdir(parents=True, exist_ok=True)
    (INSTALL_DIR / MARKER_NAME).write_text(json.dumps({"product": PRODUCT_ID, "storageVersion": 1}), encoding="utf-8")
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
    if previous_root and previous_root.resolve() != INSTALL_DIR.resolve():
        _stop_installed_app(previous_root)
    if migrate:
        migrate_user_data(previous_root)
    state_files = [INSTALL_DIR / CURRENT_NAME, INSTALL_DIR / SETUP_EXE, INSTALL_DIR / MARKER_NAME, *_shortcut_paths()]
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
    _removal_targets(INSTALL_DIR)
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
    global INSTALL_DIR
    parser = argparse.ArgumentParser(add_help=False)
    parser.add_argument("--uninstall", action="store_true")
    parser.add_argument("--update-pid", type=int)
    parser.add_argument("--silent", action="store_true")
    parser.add_argument("--no-launch", action="store_true")
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument("--install-dir", type=Path)
    parser.add_argument("--migrate-data", action="store_true")
    parser.add_argument("--native-prepare", action="store_true")
    parser.add_argument("--native-finalize")
    parser.add_argument("--native-uninstall", action="store_true")
    parser.add_argument("--error-file", type=Path)
    args, _ = parser.parse_known_args()
    try:
        if args.native_prepare or args.native_finalize or args.native_uninstall:
            if args.install_dir is None:
                raise ValueError("维护操作需要明确的安装目录")
            INSTALL_DIR = args.install_dir
            if args.native_prepare:
                prepare_native_install(args.update_pid)
            elif args.native_finalize:
                finalize_native_install(args.native_finalize)
            else:
                prepare_native_uninstall()
        elif args.verify_only:
            validate_payload(_payload_dir())
        else:
            current = installed_root()
            INSTALL_DIR = args.install_dir or current or INSTALL_DIR
            if args.uninstall:
                if not args.install_dir and getattr(sys, "frozen", False):
                    INSTALL_DIR = Path(sys.executable).resolve().parent
                validate_install_dir(INSTALL_DIR)
                if not args.silent:
                    answer = ctypes.windll.user32.MessageBoxW(
                        0, f"卸载 {INSTALL_DIR} 中的程序？\n项目数据和日志将保留在原目录。",
                        "卸载 Local Ops Windows", 0x24)
                    if answer != 6:
                        return 0
                uninstall(args.silent)
            elif not args.silent and args.update_pid is None:
                raise RuntimeError("请运行 LocalOpsWindows-Setup.exe 标准安装向导")
            else:
                install(args.update_pid, args.silent, launch=not args.no_launch,
                        migrate=args.migrate_data or args.update_pid is not None)
        return 0
    except Exception as exc:
        if args.error_file:
            args.error_file.write_text(str(exc), encoding="utf-8-sig")
        if not args.silent:
            _message_box(str(exc))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
