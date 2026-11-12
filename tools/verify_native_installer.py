#!/usr/bin/env python3
"""Run a separately identified Inno installer silently, never the production identity."""

from __future__ import annotations

import argparse
from contextlib import ExitStack
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import time
from unittest import mock
import winreg

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools import verify_installer as legacy_checks

installer = legacy_checks.installer
ROOT = legacy_checks.ROOT


def verify(setup: Path, test_id: str, payload: Path) -> None:
    if not re.fullmatch(r"[0-9a-f]{32}", test_id):
        raise ValueError("An explicit test identity is required")
    record = json.loads(setup.with_name("LocalOpsWindows.SetupBuild.json").read_text(encoding="utf-8"))
    if record.get("format") != "inno" or record.get("testId") != test_id:
        raise RuntimeError("Refusing to execute a production or mismatched installer")
    if hashlib.sha256(setup.read_bytes()).hexdigest() != record["sha256"]:
        raise RuntimeError("Test installer checksum mismatch")
    product = "LocalOpsWindows.Test." + test_id
    key = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" + product + "_is1"
    if installer._registry_snapshot(key):
        raise RuntimeError("Test identity is already registered; investigate before reusing it")
    expected_manifest = (payload / installer.MANIFEST_NAME).read_bytes()
    installer.validate_payload(payload)
    (ROOT / "tmp").mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="inno-smoke-", dir=ROOT / "tmp",
                                     ignore_cleanup_errors=True) as folder, ExitStack() as stack:
        root = Path(folder)
        target = root / "中文 空格" / "LocalOpsWindows"
        roaming = root / "roaming"
        local = root / "local"
        stack.enter_context(mock.patch.dict(os.environ, {"APPDATA": str(roaming), "LOCALAPPDATA": str(local)}))
        stack.enter_context(mock.patch.object(installer, "INSTALL_DIR", target))
        stack.enter_context(mock.patch.object(installer, "NATIVE_UNINSTALL_KEY", key))
        source = roaming / "LocalOpsWindows/config.json"
        source.parent.mkdir(parents=True)
        source.write_text(json.dumps({
            "schemaVersion": 1, "apps": [{"id": "legacy-fixture", "name": "Do not import"}],
            "settings": {},
        }), encoding="utf-8")
        original = source.read_bytes()
        preferences = source.with_name("client-settings.json")
        preferences.write_text('{"Theme":"light"}', encoding="utf-8")
        old_log = local / "LocalOpsWindows/logs/legacy.log"
        old_log.parent.mkdir(parents=True)
        old_log.write_bytes(b"legacy log fixture")
        environment = dict(os.environ, TEMP=str(root / "bootstrap"), TMP=str(root / "bootstrap"))
        (root / "bootstrap").mkdir()

        def run(executable: Path, arguments: list[str]) -> None:
            result = subprocess.run([str(executable), *arguments], env=environment,
                                    timeout=180, creationflags=subprocess.CREATE_NO_WINDOW)
            if result.returncode:
                diagnostics = setup.parent / "diagnostics"
                diagnostics.mkdir(exist_ok=True)
                for log in root.glob("*.log"):
                    shutil.copy2(log, diagnostics / log.name)
                raise RuntimeError(f"Installer failed ({result.returncode}); logs: {diagnostics}")

        def uninstall() -> None:
            executable = target / "unins000.exe"
            if executable.exists():
                run(executable, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                                 f"/LOG={root / 'uninstall.log'}"])
                legacy_checks.wait_for(lambda: not installer._registry_snapshot(key))
                # Inno's original uninstaller exits before its cleanup child.
                deadline = time.monotonic() + 10
                while time.monotonic() < deadline and any((root / "bootstrap").glob("is-*-uninstall.tmp")):
                    time.sleep(0.2)

        try:
            run(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                        f"/DIR={target}", "/TASKS=desktopicon,autostart",
                        f"/LOG={root / 'install.log'}"])
            registration = installer._registry_snapshot(key)
            assert Path(registration["InstallLocation"][0]).resolve() == target.resolve()
            assert not (target / "data").exists(), "Setup copied data before the first launch"
            assert not (target / "logs").exists(), "Setup copied historical logs"
            assert source.read_bytes() == original
            installed = installer._current_executable().parent
            installer.validate_payload(installed)
            assert (installed / installer.MANIFEST_NAME).read_bytes() == expected_manifest
            legacy_checks.verify_archive_privacy(
                installed, target / installer.MAINTENANCE_EXE, embedded_payload=False)
            assert (target / "test-shortcuts/Start.lnk").is_file()
            assert (target / "test-shortcuts/Desktop.lnk").is_file()
            run_values = installer._registry_snapshot(installer.RUN_KEY)
            assert "--background" in run_values[product][0]
            assert str(installed) in run_values[product][0]
            legacy_checks.verify_backend(installed, target, expect_empty=True)
            assert not (target / "data/client-settings.json").exists()
            assert not (target / "logs/legacy.log").exists()
            config = (target / "data/config.json").read_bytes()
            run(setup, ["/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART",
                        f"/DIR={target}", "/TASKS=", f"/LOG={root / 'reinstall.log'}"])
            assert product not in installer._registry_snapshot(installer.RUN_KEY)
            assert (target / "data/config.json").read_bytes() == config
            unknown = target / "user-file.txt"
            unknown.write_text("keep", encoding="utf-8")
            uninstall()
            assert not installer._registry_snapshot(key)
            assert product not in installer._registry_snapshot(installer.RUN_KEY)
            assert not (target / "cache").exists()
            assert not (installed / installer.MAIN_EXE).exists()
            assert not (target / "current.json").exists()
            assert not (target / "test-shortcuts/Start.lnk").exists()
            assert (target / "data/config.json").read_bytes() == config
            assert (target / "logs/console.log").stat().st_size
            assert unknown.read_text(encoding="utf-8") == "keep"
            assert source.read_bytes() == original
            assert preferences.read_text(encoding="utf-8") == '{"Theme":"light"}'
            assert old_log.read_bytes() == b"legacy log fixture"
            print("PASS: real Inno test package, custom directory, clean first run, tasks, backend, reinstall and uninstall")
        finally:
            installer._stop_installed_app()
            if installer._registry_snapshot(key):
                uninstall()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--setup", type=Path, required=True)
    parser.add_argument("--test-identity", required=True)
    parser.add_argument("--payload", type=Path, required=True)
    args = parser.parse_args()
    verify(args.setup.resolve(), args.test_identity, args.payload.resolve())
