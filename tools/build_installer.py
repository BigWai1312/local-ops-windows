#!/usr/bin/env python3
"""Build the Windows application, anchor helper and per-user setup EXE."""

from __future__ import annotations

import argparse
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUTPUT = ROOT / "dist"
PYINSTALLER = [sys.executable, "-m", "PyInstaller"]
OPTIONAL_MODULES = (
    # Only the standard-library backend is frozen; WPF supplies the desktop UI.
    "webview",
    "pystray",
    "PyQt5",
    "PyQt6",
    "PySide2",
    "PySide6",
    "qtpy",
    "cefpython3",
    "gi",
    "gtk",
    "wx",
)


def run(command: list[str]) -> None:
    print("+", " ".join(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def version() -> str:
    value = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    if not value or any(ch not in "0123456789.+-" for ch in value):
        raise SystemExit("VERSION 无效")
    return value


def build_binary(script: Path, name: str, work: Path, output: Path,
                 extra: list[str] | None = None, onefile: bool = True) -> Path:
    build_root = work / name
    build_root.mkdir(parents=True, exist_ok=True)
    command = PYINSTALLER + [
        "--noconfirm", "--clean", "--onefile" if onefile else "--onedir", "--windowed",
        "--name", name, "--distpath", str(output),
        "--workpath", str(build_root / "work"), "--specpath", str(build_root),
    ]
    command += extra or []
    for module in OPTIONAL_MODULES:
        command += ["--exclude-module", module]
    command.append(str(script))
    run(command)
    result = output / (name + ".exe") if onefile else output / name / (name + ".exe")
    if not result.is_file():
        raise SystemExit(f"PyInstaller 未生成 {result}")
    return result


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def copy_runtime_licenses(payload: Path, work: Path) -> None:
    destination = payload / "licenses" / "runtime"
    destination.mkdir(parents=True, exist_ok=True)
    assets = json.loads((work / "dotnet/obj/LocalOpsWindows.Client/project.assets.json").read_text())
    frameworks = json.loads((payload / "LocalOpsWindows.Client.runtimeconfig.json").read_text())
    for framework in frameworks["runtimeOptions"]["includedFrameworks"]:
        package = framework["name"].lower() + ".runtime.win-x64"
        candidates = [Path(folder) / package / framework["version"]
                      for folder in assets["packageFolders"]]
        source = next((path for path in candidates if path.is_dir()), None)
        if source is None:
            raise RuntimeError(f"Missing runtime license package: {package}")
        license_files = [path for path in source.iterdir()
                         if path.name.upper() in {"LICENSE", "LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"}]
        if not license_files:
            raise RuntimeError(f"Missing runtime license: {package}")
        for path in license_files:
            shutil.copy2(path, destination / (package + "-" + path.name))
    python_license = next((Path(sys.base_prefix) / name for name in ("LICENSE.txt", "LICENSE_PYTHON.txt")
                           if (Path(sys.base_prefix) / name).is_file()), None)
    if python_license is None:
        raise RuntimeError("Missing Python runtime license")
    shutil.copy2(python_license, destination / "Python-LICENSE.txt")
    package = importlib.metadata.distribution("pyinstaller")
    pyinstaller_license = package.locate_file(next(
        file for file in package.files or [] if file.name == "COPYING.txt"))
    shutil.copy2(pyinstaller_license, destination / "PyInstaller-COPYING.txt")


def build(output_dir: Path) -> list[Path]:
    output_dir.mkdir(parents=True, exist_ok=True)
    work = ROOT / "build" / "csharp-installer"
    if not work.resolve().is_relative_to((ROOT / "build").resolve()):
        raise SystemExit("构建目录越界")
    shutil.rmtree(work, ignore_errors=True)
    temp_dist = work / "dist"
    temp_dist.mkdir(parents=True, exist_ok=True)

    payload = work / "payload"
    run(["dotnet", "publish",
         str(ROOT / "csharp/LocalOpsWindows.Client/LocalOpsWindows.Client.csproj"),
         "-c", "Release", "-r", "win-x64", "--self-contained", "true",
         "-o", str(payload), "--artifacts-path", str(work / "dotnet"), f"-p:Version={version()}",
         "-p:DebugType=None", "-p:DebugSymbols=false",
         f"-p:PathMap={ROOT}=/_/src", "-p:ContinuousIntegrationBuild=true"])
    backend = build_binary(
        ROOT / "server.py", "LocalOpsWindowsBackend", work, temp_dist,
        ["--icon", str(ROOT / "csharp/LocalOpsWindows.Client/Assets/local-ops.ico"),
         "--add-data", f"{ROOT / 'static'};static",
         "--add-data", f"{ROOT / 'VERSION'};."], onefile=False)
    shutil.copytree(backend.parent, payload / "backend")
    anchor = build_binary(
        ROOT / "tools" / "win_anchor.py", "LocalOpsWindowsAnchor", work,
        temp_dist)
    shutil.copy2(anchor, payload / "backend" / anchor.name)
    for name in ("LICENSE", "NOTICE.md", "THIRD_PARTY_NOTICES.md"):
        shutil.copy2(ROOT / name, payload / name)
    shutil.copytree(ROOT / "licenses", payload / "licenses", dirs_exist_ok=True)
    copy_runtime_licenses(payload, work)
    manifest = {
        "product": "LocalOpsWindows.CSharp", "version": version(),
        "files": {path.relative_to(payload).as_posix(): sha256(path)
                  for path in sorted(payload.rglob("*")) if path.is_file()},
    }
    (payload / "payload.json").write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    # Validate the actual frozen payload with the same checks the installer runs.
    sys.path.insert(0, str(ROOT))
    from installer import validate_payload
    validate_payload(payload)
    setup = build_binary(
        ROOT / "installer.py", "LocalOpsWindows-Setup", work, temp_dist,
        ["--icon", str(ROOT / "csharp/LocalOpsWindows.Client/Assets/local-ops.ico"),
         "--add-data", f"{payload};payload"])

    artifacts = []
    for source in (setup,):
        destination = output_dir / source.name
        shutil.copy2(source, destination)
        checksum = output_dir / (source.name + ".sha256")
        checksum.write_text(f"{sha256(destination)}  {destination.name}\n", encoding="utf-8")
        artifacts.extend((destination, checksum))
    resource = output_dir / "LocalOpsWindows.Resources.json"
    shutil.copy2(ROOT / "csharp/LocalOpsWindows.Client/resources.json", resource)
    resource_data = json.loads(resource.read_text(encoding="utf-8"))
    update = {
        "schemaVersion": 1, "product": "LocalOpsWindows.CSharp", "clientVersion": version(),
        "installer": {"asset": setup.name, "sha256": sha256(output_dir / setup.name)},
        "resources": {"asset": resource.name, "sha256": sha256(resource),
                      "revision": resource_data["revision"],
                      "minClientVersion": resource_data["minClientVersion"],
                      "maxClientVersion": resource_data["maxClientVersion"]},
    }
    update_path = output_dir / "LocalOpsWindows.Update.json"
    update_path.write_text(json.dumps(update, indent=2) + "\n", encoding="utf-8")
    artifacts.extend((resource, update_path))
    print("已生成安装包：", output_dir / setup.name)
    print("SHA-256：", sha256(output_dir / setup.name))
    return artifacts


def main() -> int:
    parser = argparse.ArgumentParser(description="构建 Local Ops Windows 安装包")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()
    build(args.output.resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
