#!/usr/bin/env python3
"""Build the Windows application, anchor helper and per-user setup EXE."""

from __future__ import annotations

import argparse
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUTPUT = ROOT / "dist"
PYINSTALLER = [sys.executable, "-m", "PyInstaller"]


def run(command: list[str]) -> None:
    print("+", " ".join(command), flush=True)
    subprocess.run(command, cwd=ROOT, check=True)


def version() -> str:
    value = (ROOT / "VERSION").read_text(encoding="utf-8").strip()
    if not value or any(ch not in "0123456789.+-" for ch in value):
        raise SystemExit("VERSION 无效")
    return value


def build_binary(script: Path, name: str, work: Path, output: Path,
                 extra: list[str] | None = None) -> Path:
    build_root = work / name
    build_root.mkdir(parents=True, exist_ok=True)
    command = PYINSTALLER + [
        "--noconfirm", "--clean", "--onefile", "--windowed",
        "--name", name, "--distpath", str(output),
        "--workpath", str(build_root / "work"), "--specpath", str(build_root),
    ]
    command += extra or []
    command.append(str(script))
    run(command)
    result = output / (name + ".exe")
    if not result.is_file():
        raise SystemExit(f"PyInstaller 未生成 {result}")
    return result


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def build(output_dir: Path) -> list[Path]:
    output_dir.mkdir(parents=True, exist_ok=True)
    work = ROOT / "build" / "pyinstaller"
    shutil.rmtree(work, ignore_errors=True)
    temp_dist = work / "dist"
    temp_dist.mkdir(parents=True, exist_ok=True)

    app = build_binary(
        ROOT / "server.py", "LocalOpsWindows", work, temp_dist,
        ["--icon", str(ROOT / "static" / "assets" / "favicon.ico"),
         "--add-data", f"{ROOT / 'static'};static",
         "--add-data", f"{ROOT / 'VERSION'};."])
    anchor = build_binary(
        ROOT / "tools" / "win_anchor.py", "LocalOpsWindowsAnchor", work,
        temp_dist)
    setup = build_binary(
        ROOT / "installer.py", "LocalOpsWindows-Setup", work, temp_dist,
        ["--icon", str(ROOT / "static" / "assets" / "favicon.ico"),
         "--add-binary", f"{app};payload",
         "--add-binary", f"{anchor};payload"])

    artifacts = []
    for source in (setup,):
        destination = output_dir / source.name
        shutil.copy2(source, destination)
        checksum = output_dir / (source.name + ".sha256")
        checksum.write_text(f"{sha256(destination)}  {destination.name}\n", encoding="utf-8")
        artifacts.extend((destination, checksum))
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
