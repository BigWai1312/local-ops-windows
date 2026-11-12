import hashlib
import json
import os
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest import mock

import installer
import winreg
from tools import build_installer
from tools import verify_native_installer


class InstallerTests(unittest.TestCase):
    def setUp(self):
        scratch = Path(__file__).resolve().parents[1] / "tmp"
        scratch.mkdir(exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=scratch)
        self.root = Path(self.temp.name)
        self.payload = self.root / "payload"
        self.target = self.root / "中文 安装目录"
        self.shortcuts = (self.root / "Start.lnk", self.root / "Desktop.lnk")
        self.registry = r"Software\LocalOpsWindows.Tests" + "\\" + uuid.uuid4().hex
        self.patches = [
            mock.patch.object(installer, "INSTALL_DIR", self.target),
            mock.patch.object(installer, "_payload_dir", return_value=self.payload),
            mock.patch.object(installer, "_shortcut_paths", return_value=self.shortcuts),
            mock.patch.object(installer, "RUN_KEY", self.registry + r"\Run"),
            mock.patch.object(installer, "UNINSTALL_KEY", self.registry + r"\Uninstall"),
            mock.patch.object(installer, "NATIVE_UNINSTALL_KEY", self.registry + r"\NativeUninstall"),
            mock.patch.object(installer, "SETTINGS_KEY", self.registry + r"\Client"),
            mock.patch.object(installer, "_stop_installed_app"),
            mock.patch.object(installer, "_remove_legacy_scheduled_task"),
            mock.patch.object(installer, "_launch_main"),
            mock.patch.object(installer, "_create_shortcut",
                              side_effect=lambda path, target: path.write_text(str(target))),
        ]
        for patch in self.patches:
            patch.start()
        self.write_payload()

    def tearDown(self):
        for path in (self.registry + r"\Run", self.registry + r"\Uninstall",
                     self.registry + r"\Client", self.registry + r"\NativeUninstall", self.registry):
            try:
                winreg.DeleteKey(winreg.HKEY_CURRENT_USER, path)
            except FileNotFoundError:
                pass
        for patch in reversed(self.patches):
            patch.stop()
        self.temp.cleanup()

    def write_payload(self, version="2.1.0"):
        for name in (installer.MAIN_EXE, "backend/LocalOpsWindowsBackend.exe",
                     "backend/" + installer.ANCHOR_EXE, "LICENSE", "NOTICE.md",
                     "THIRD_PARTY_NOTICES.md", "Assets/local-ops.ico", "resources.json",
                     "LocalOpsWindows.Client.dll", "LocalOpsWindows.Client.runtimeconfig.json",
                     "LocalOpsWindows.Client.deps.json", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll"):
            path = self.payload / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(("fixture-" + version).encode())
        manifest = {
            "product": "LocalOpsWindows.CSharp", "version": version,
            "files": {p.relative_to(self.payload).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
                      for p in self.payload.rglob("*") if p.is_file() and p.name != installer.MANIFEST_NAME},
        }
        (self.payload / installer.MANIFEST_NAME).write_text(json.dumps(manifest))

    def test_manifest_rejects_tampering_unlisted_files_and_path_escape(self):
        self.assertEqual(installer.validate_payload(self.payload)["version"], "2.1.0")
        (self.payload / installer.MAIN_EXE).write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "完整性"):
            installer.validate_payload(self.payload)
        self.write_payload()
        rogue = self.payload / "unlisted.exe"
        rogue.write_bytes(b"extra")
        with self.assertRaisesRegex(ValueError, "未登记"):
            installer.validate_payload(self.payload)
        rogue.unlink()
        path = self.payload / installer.MANIFEST_NAME
        manifest = json.loads(path.read_text())
        manifest["files"]["../outside.exe"] = "0" * 64
        path.write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "路径"):
            installer.validate_payload(self.payload)

    def test_upgrade_preserves_old_versions_user_data_and_disabled_autostart(self):
        installer.install(launch=False)
        data = self.target / "data/config.json"
        data.parent.mkdir()
        data.write_bytes(b"private-fixture")
        self.assertEqual(installer.installed_root(), self.target)
        first = installer._current_executable()
        self.assertEqual(first.read_bytes(), b"fixture-2.1.0")
        installer._set_autostart(False)
        self.write_payload("2.1.1")
        installer.install(launch=False)
        second = installer._current_executable()
        self.assertNotEqual(first, second)
        self.assertTrue(first.is_file())
        self.assertEqual(second.read_bytes(), b"fixture-2.1.1")
        self.assertNotIn(installer.PRODUCT_ID, installer._registry_snapshot(installer.RUN_KEY))
        self.assertEqual(data.read_bytes(), b"private-fixture")
        self.assertEqual(self.shortcuts[0].read_text(), str(second))
        installer._launch_main.assert_not_called()

    def test_failed_shortcut_creation_rolls_back_current_and_registry(self):
        installer.install(launch=False)
        old_current = (self.target / installer.CURRENT_NAME).read_bytes()
        old_run = installer._registry_snapshot(installer.RUN_KEY)
        old_shortcut = self.shortcuts[0].read_bytes()
        self.write_payload("2.1.1")
        with mock.patch.object(installer, "_create_shortcut", side_effect=OSError("fixture failure")):
            with self.assertRaisesRegex(OSError, "fixture failure"):
                installer.install(launch=False)
        self.assertEqual((self.target / installer.CURRENT_NAME).read_bytes(), old_current)
        self.assertEqual(installer._registry_snapshot(installer.RUN_KEY), old_run)
        self.assertEqual(self.shortcuts[0].read_bytes(), old_shortcut)

    def test_failed_pid_wait_does_not_stop_any_existing_process(self):
        with mock.patch.object(installer, "_wait_for_pid", return_value=False):
            with self.assertRaisesRegex(RuntimeError, "未能退出"):
                installer.install(update_pid=1234, launch=False)
        installer._stop_installed_app.assert_not_called()
        self.assertFalse((self.target / installer.CURRENT_NAME).exists())

    def test_uninstall_removal_rejects_unrelated_directory(self):
        with self.assertRaisesRegex(RuntimeError, "拒绝删除"):
            installer._schedule_remove(self.root)

    def test_uninstall_helper_actually_removes_only_install_directory(self):
        installer.install(launch=False)
        preserved = self.target / "data/config.json"
        preserved.parent.mkdir()
        preserved.write_bytes(b"private-fixture")
        unknown = self.target / "fixture.txt"
        unknown.write_bytes(b"user file")
        logs = self.target / "logs"
        logs.mkdir()
        (logs / "console.log").write_text("fixture log")
        cache = self.target / "cache"
        cache.mkdir()
        (cache / "download.exe").write_bytes(b"cache")
        process = installer.uninstall(silent=True)
        self.assertEqual(process.wait(timeout=15), 0)
        self.assertFalse((self.target / "versions").exists())
        self.assertFalse((self.target / installer.SETUP_EXE).exists())
        self.assertFalse(cache.exists())
        self.assertTrue((logs / "console.log").is_file())
        self.assertEqual(unknown.read_bytes(), b"user file")
        self.assertEqual(preserved.read_bytes(), b"private-fixture")
        self.assertFalse(installer._registry_snapshot(installer.UNINSTALL_KEY))
        installer.install(launch=False)
        self.assertEqual(preserved.read_bytes(), b"private-fixture")

    def test_custom_directory_rejects_unrelated_content_and_root(self):
        unrelated = self.root / "unrelated"
        unrelated.mkdir()
        (unrelated / "keep.txt").write_text("keep")
        for path in (unrelated, Path(self.root.anchor), Path("relative")):
            with self.assertRaises(ValueError):
                installer.validate_install_dir(path)

    def test_native_install_checks_before_stopping_and_preserves_data(self):
        unrelated = self.root / "unrelated"
        unrelated.mkdir()
        (unrelated / "keep.txt").write_text("keep")
        with mock.patch.object(installer, "INSTALL_DIR", unrelated):
            with self.assertRaises(ValueError):
                installer.prepare_native_install()
        installer._stop_installed_app.assert_not_called()
        installer.prepare_native_install()
        marker = json.loads((self.target / installer.MARKER_NAME).read_text())
        self.assertEqual(marker["product"], installer.PRODUCT_ID)
        self.assertFalse((self.target / installer.CURRENT_NAME).exists())
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, installer.NATIVE_UNINSTALL_KEY) as key:
            winreg.SetValueEx(key, "InstallLocation", 0, winreg.REG_SZ, str(self.target))
        self.assertEqual(installer.installed_root(), self.target)
        with mock.patch.object(installer, "INSTALL_DIR", self.root / "other"):
            with self.assertRaisesRegex(RuntimeError, "沿用"):
                installer.prepare_native_install()
        self.assertFalse((self.root / "other").exists())

    def test_native_finalize_validates_payload_before_switching_current(self):
        import shutil
        installer.prepare_native_install()
        version_name = "2.1.0-" + installer._hash(self.payload / installer.MANIFEST_NAME)[:12]
        destination = self.target / "versions" / version_name
        shutil.copytree(self.payload, destination)
        with self.assertRaisesRegex(RuntimeError, "注册"):
            installer.finalize_native_install(version_name)
        self.assertFalse((self.target / installer.CURRENT_NAME).exists())
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, installer.NATIVE_UNINSTALL_KEY) as key:
            winreg.SetValueEx(key, "InstallLocation", 0, winreg.REG_SZ, str(self.target))
        installer.finalize_native_install(version_name)
        old = (self.target / installer.CURRENT_NAME).read_bytes()
        self.assertEqual(installer._current_executable(), destination / installer.MAIN_EXE)
        (destination / installer.MAIN_EXE).write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "完整性"):
            installer.finalize_native_install(version_name)
        self.assertEqual((self.target / installer.CURRENT_NAME).read_bytes(), old)
        installer._remove_legacy_scheduled_task.assert_not_called()

    def test_native_prepare_does_not_migrate_historical_data(self):
        legacy = self.root / "legacy"
        legacy.mkdir()
        original = legacy / "config.json"
        original.write_bytes(b'{"apps":[{"id":"legacy-fixture"}]}')
        with mock.patch.object(installer, "installed_root", return_value=legacy), \
                mock.patch.object(installer, "migrate_user_data") as migrate:
            installer.prepare_native_install()
        migrate.assert_not_called()
        self.assertFalse((self.target / "data").exists())
        self.assertFalse((self.target / "logs").exists())
        self.assertEqual(original.read_bytes(), b'{"apps":[{"id":"legacy-fixture"}]}')

    def test_native_cli_ignores_historical_migration_option(self):
        with mock.patch.object(installer.sys, "argv", [
                "helper", "--native-prepare", "--silent", "--migrate-data",
                "--install-dir", str(self.target)]), \
                mock.patch.object(installer, "prepare_native_install") as prepare:
            self.assertEqual(installer.main(), 0)
        prepare.assert_called_once_with(None)

    def test_native_uninstall_does_not_delete_user_data_or_program_root(self):
        installer.prepare_native_install()
        for name in ("data/config.json", "logs/console.log", "cache/download.tmp", "unknown.txt"):
            path = self.target / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("keep")
        installer.prepare_native_uninstall()
        self.assertFalse((self.target / "cache").exists())
        for name in ("data/config.json", "logs/console.log", "unknown.txt"):
            self.assertEqual((self.target / name).read_text(), "keep")
        self.assertTrue(self.target.is_dir())

    def test_native_cli_requires_directory_and_reports_silent_errors(self):
        error = self.root / "error.txt"
        with mock.patch.object(installer.sys, "argv", [
                "helper", "--native-prepare", "--silent", "--error-file", str(error)]), \
                mock.patch.object(installer, "prepare_native_install") as prepare, \
                mock.patch.object(installer, "_message_box") as message:
            self.assertEqual(installer.main(), 1)
        self.assertIn("安装目录", error.read_text(encoding="utf-8-sig"))
        prepare.assert_not_called()
        message.assert_not_called()

    def test_cli_keeps_update_directory_and_migrates_legacy_data(self):
        installer.install(launch=False)
        with mock.patch.object(installer.sys, "argv", [
                "setup", "--update-pid", "1234", "--no-launch"]), \
                mock.patch.object(installer, "install") as install:
            self.assertEqual(installer.main(), 0)
            install.assert_called_once_with(1234, False, launch=False, migrate=True)
            self.assertEqual(installer.INSTALL_DIR, self.target)

    def test_old_installation_cannot_unregister_relocated_install(self):
        installer.install(launch=False)
        old_target = self.target
        new_target = self.root / "moved"
        with mock.patch.object(installer, "INSTALL_DIR", new_target):
            installer.install(launch=False)
        before = installer._registry_snapshot(installer.UNINSTALL_KEY)
        with self.assertRaisesRegex(RuntimeError, "当前注册"):
            installer.uninstall(silent=True)
        self.assertEqual(installer._registry_snapshot(installer.UNINSTALL_KEY), before)
        self.assertTrue((old_target / "versions").is_dir())

    def test_migration_keeps_originals_and_excludes_instance_lock(self):
        legacy = self.root / "legacy"
        (legacy / "data/icons").mkdir(parents=True)
        (legacy / "data/config.json").write_text('{"apps":[]}')
        (legacy / "data/console.lock").write_text("active lock")
        (legacy / "data/icons/fixture.png").write_bytes(b"fixture")
        (legacy / "logs").mkdir()
        (legacy / "logs/console.log").write_text("old log")
        with winreg.CreateKey(winreg.HKEY_CURRENT_USER, installer.SETTINGS_KEY) as key:
            winreg.SetValueEx(key, "Theme", 0, winreg.REG_SZ, "dark")
        installer.install(launch=False)
        installer.migrate_user_data(legacy)
        self.assertEqual((self.target / "data/config.json").read_bytes(),
                         (legacy / "data/config.json").read_bytes())
        self.assertFalse((self.target / "data/console.lock").exists())
        self.assertTrue((legacy / "data/console.lock").exists())
        self.assertEqual(json.loads((self.target / "data/client-settings.json").read_text())["Theme"], "dark")
        self.assertEqual((self.target / "logs/console.log").read_text(), "old log")
        (self.target / "data/config.json").write_text('{"apps":[{"name":"keep"}]}')
        installer.migrate_user_data(legacy)
        self.assertIn("keep", (self.target / "data/config.json").read_text())

    def test_junction_in_program_tree_prevents_uninstall_before_registry_changes(self):
        installer.install(launch=False)
        external = self.root / "external"
        external.mkdir()
        (external / "keep.txt").write_text("keep")
        junction = self.target / "cache"
        command = f"New-Item -ItemType Junction -Path {installer._ps_quote(junction)} -Target {installer._ps_quote(external)} | Out-Null"
        installer.subprocess.run(["powershell.exe", "-NoProfile", "-NonInteractive", "-Command", command],
                                 check=True, creationflags=installer.CREATE_NO_WINDOW)
        try:
            before = installer._registry_snapshot(installer.UNINSTALL_KEY)
            with self.assertRaisesRegex(ValueError, "链接"):
                installer.uninstall(silent=True)
            self.assertEqual(installer._registry_snapshot(installer.UNINSTALL_KEY), before)
            self.assertEqual((external / "keep.txt").read_text(), "keep")
        finally:
            os.rmdir(junction)


class NativeBuildTests(unittest.TestCase):
    def test_compiler_discovery_reads_custom_registered_location(self):
        with tempfile.TemporaryDirectory() as folder:
            executable = Path(folder) / "ISCC.exe"
            executable.write_bytes(b"fixture")
            with mock.patch.dict(os.environ, {}, clear=True), \
                    mock.patch.object(build_installer.shutil, "which", return_value=None), \
                    mock.patch.object(winreg, "OpenKey"), \
                    mock.patch.object(winreg, "QueryValueEx", return_value=(folder, winreg.REG_SZ)):
                self.assertEqual(build_installer.find_inno_compiler(), executable.resolve())

    def test_missing_compiler_fails_before_mutating_build_or_output(self):
        with mock.patch.object(build_installer, "find_inno_compiler", side_effect=RuntimeError("missing compiler")), \
                mock.patch.object(build_installer.shutil, "rmtree") as remove, \
                mock.patch.object(Path, "mkdir") as mkdir, \
                mock.patch.object(build_installer, "run") as run:
            with self.assertRaisesRegex(RuntimeError, "missing compiler"):
                build_installer.build(Path("unused"))
        remove.assert_not_called()
        mkdir.assert_not_called()
        run.assert_not_called()

    def test_verifier_rejects_production_identity_before_executing(self):
        with tempfile.TemporaryDirectory() as folder:
            setup = Path(folder) / "setup.exe"
            setup.with_name("LocalOpsWindows.SetupBuild.json").write_text(
                json.dumps({"format": "inno", "testId": None}))
            with mock.patch.object(verify_native_installer.subprocess, "run") as run:
                with self.assertRaisesRegex(ValueError, "test identity"):
                    verify_native_installer.verify(setup, "", Path(folder))
                with self.assertRaisesRegex(RuntimeError, "production"):
                    verify_native_installer.verify(setup, "a" * 32, Path(folder))
                setup.write_bytes(b"fixture")
                setup.with_name("LocalOpsWindows.SetupBuild.json").write_text(json.dumps({
                    "format": "inno", "testId": "a" * 32, "sha256": "0" * 64}))
                with self.assertRaisesRegex(RuntimeError, "checksum"):
                    verify_native_installer.verify(setup, "a" * 32, Path(folder))
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
