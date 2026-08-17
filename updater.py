"""GitHub Release update client for the installed Windows application."""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path
import re
import tempfile
import urllib.parse
import urllib.error
import urllib.request


REPOSITORY = "BigWai1312/local-ops-windows"
RELEASES_API = f"https://api.github.com/repos/{REPOSITORY}/releases/latest"
INSTALLER_ASSET = "LocalOpsWindows-Setup.exe"
CHECKSUM_ASSET = f"{INSTALLER_ASSET}.sha256"
USER_AGENT = "LocalOpsWindows-updater/1"
MAX_DOWNLOAD_BYTES = 250 * 1024 * 1024
VERSION_RE = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$")
ALLOWED_RELEASE_HOSTS = {"github.com", "objects.githubusercontent.com"}


def version_key(value: str) -> tuple[int, int, int]:
    match = VERSION_RE.fullmatch(str(value).strip().lstrip("vV"))
    if not match:
        return (0, 0, 0)
    return tuple(int(part) for part in match.groups())


def _request_json(url: str, timeout: float = 12.0) -> dict:
    request = urllib.request.Request(
        url,
        headers={
            "Accept": "application/vnd.github+json",
            "User-Agent": USER_AGENT,
        },
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        payload = response.read(2 * 1024 * 1024)
    data = json.loads(payload.decode("utf-8"))
    if not isinstance(data, dict):
        raise ValueError("GitHub Release 响应格式无效")
    return data


def latest_release() -> dict:
    """Return the latest stable release metadata needed by the UI."""
    raw = _request_json(RELEASES_API)
    assets = {}
    for item in raw.get("assets") or []:
        if not isinstance(item, dict):
            continue
        name = item.get("name")
        url = item.get("browser_download_url")
        if isinstance(name, str) and isinstance(url, str) and _allowed_asset_url(url):
            assets[name] = {
                "name": name,
                "url": url,
                "size": item.get("size"),
            }
    tag = str(raw.get("tag_name") or "").strip()
    version = tag.lstrip("vV")
    if not VERSION_RE.fullmatch(version):
        raise ValueError("GitHub Release 版本号无效")
    if INSTALLER_ASSET not in assets or CHECKSUM_ASSET not in assets:
        raise ValueError("GitHub Release 缺少安装包或 SHA-256 文件")
    return {
        "tag": tag,
        "version": version,
        "name": str(raw.get("name") or tag),
        "htmlUrl": str(raw.get("html_url") or ""),
        "publishedAt": raw.get("published_at"),
        "installer": assets[INSTALLER_ASSET],
        "checksum": assets[CHECKSUM_ASSET],
    }


def _allowed_asset_url(url: str) -> bool:
    """Only accept direct assets from the configured GitHub repository."""
    try:
        parsed = urllib.parse.urlparse(url)
    except ValueError:
        return False
    if parsed.scheme != "https" or (parsed.hostname or "").lower() not in ALLOWED_RELEASE_HOSTS:
        return False
    if parsed.hostname == "github.com":
        expected = f"/{REPOSITORY}/releases/download/"
        return parsed.path.startswith(expected)
    return parsed.path.startswith("/objects/") or "github" in parsed.netloc.lower()


def check(current_version: str) -> dict:
    """Return a JSON-safe update status object."""
    try:
        release = latest_release()
        available = version_key(release["version"]) > version_key(current_version)
        return {
            "ok": True,
            "currentVersion": current_version,
            "latestVersion": release["version"],
            "updateAvailable": available,
            "release": release,
        }
    except (OSError, ValueError, TypeError, json.JSONDecodeError,
            urllib.error.URLError) as exc:
        return {
            "ok": False,
            "currentVersion": current_version,
            "updateAvailable": False,
            "error": str(exc),
        }


def _download(url: str, destination: Path) -> None:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = destination.with_suffix(destination.suffix + ".tmp")
    total = 0
    try:
        with urllib.request.urlopen(request, timeout=30) as response, \
                temporary.open("wb") as stream:
            while True:
                block = response.read(1024 * 1024)
                if not block:
                    break
                total += len(block)
                if total > MAX_DOWNLOAD_BYTES:
                    raise ValueError("更新包超过大小限制")
                stream.write(block)
        os.replace(temporary, destination)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def download_and_verify(release: dict, updates_dir: str | os.PathLike[str]) -> Path:
    """Download the installer and its checksum, then return a verified path."""
    target_dir = Path(updates_dir) / release["version"]
    target_dir.mkdir(parents=True, exist_ok=True)
    installer = target_dir / INSTALLER_ASSET
    checksum = target_dir / CHECKSUM_ASSET
    _download(release["installer"]["url"], installer)
    _download(release["checksum"]["url"], checksum)
    expected = checksum.read_text(encoding="utf-8", errors="replace").split()
    if not expected or not re.fullmatch(r"[0-9a-fA-F]{64}", expected[0]):
        raise ValueError("更新包 SHA-256 文件格式无效")
    actual = _sha256(installer)
    if actual.lower() != expected[0].lower():
        raise ValueError("更新包 SHA-256 校验失败")
    return installer


def temporary_update_dir() -> Path:
    return Path(os.environ.get("LOCALAPPDATA") or tempfile.gettempdir()) / \
        "LocalOpsWindows" / "updates"
