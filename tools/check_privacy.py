#!/usr/bin/env python3
"""Fail when source files contain local identity or sensitive runtime files."""

from __future__ import annotations

import os
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
EXCLUDED_DIRS = {
    ".git", ".venv", "__pycache__", "build", "data", "dist", "logs",
    "node_modules", "release", "tmp", "venv",
}
SENSITIVE_NAMES = {
    ".env", "auth.json", "config.json", "credentials.json", "secrets.json",
    "token.json",
}
TEXT_SUFFIXES = {
    ".bat", ".css", ".html", ".ini", ".js", ".json", ".md", ".mjs",
    ".ps1", ".py", ".toml", ".txt", ".vbs", ".yaml", ".yml",
}
PLACEHOLDER_USERS = {"example", "alice", "bob", "john", "runneradmin"}
WINDOWS_HOME_RE = re.compile(r"(?i)[A-Z]:[\\/]+Users[\\/]+([^\\/\s\"'<>]+)")
EMAIL_RE = re.compile(r"(?i)\b[A-Z0-9._%+-]+@([A-Z0-9.-]+\.[A-Z]{2,})\b")


def iter_files():
    for path in ROOT.rglob("*"):
        if not path.is_file() or any(part.casefold() in EXCLUDED_DIRS for part in path.parts):
            continue
        if path.suffix.casefold() in TEXT_SUFFIXES or path.name in {"LICENSE", "Makefile", "VERSION"}:
            yield path


def scan() -> list[str]:
    failures: list[str] = []
    local_markers = {
        value.casefold()
        for value in (
            os.environ.get("USERNAME"),
            os.environ.get("COMPUTERNAME"),
            str(Path.home()),
        )
        if value and len(value) >= 4
    }
    for path in iter_files():
        relative = path.relative_to(ROOT).as_posix()
        if path.name.casefold() in SENSITIVE_NAMES:
            failures.append(f"敏感文件不应进入仓库: {relative}")
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except UnicodeError:
            continue
        lowered = text.casefold()
        for marker in sorted(local_markers):
            if marker in lowered:
                failures.append(f"发现本机身份信息: {relative} ({marker})")
        for match in WINDOWS_HOME_RE.finditer(text):
            username = match.group(1).casefold()
            if username not in PLACEHOLDER_USERS:
                failures.append(f"发现个人 Windows 路径: {relative} ({match.group(0)})")
        for match in EMAIL_RE.finditer(text):
            domain = match.group(1).casefold()
            if domain not in {"example.com", "users.noreply.github.com"}:
                failures.append(f"发现可能的个人邮箱: {relative} ({match.group(0)})")
    return sorted(set(failures))


def main() -> int:
    failures = scan()
    if failures:
        print("隐私扫描失败：")
        for failure in failures:
            print("- " + failure)
        return 1
    print("隐私扫描通过：未发现本机身份、个人路径、个人邮箱或敏感运行文件。")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
