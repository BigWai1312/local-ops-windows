import hashlib
import json
from pathlib import Path
import tempfile
import unittest
import uuid
from unittest import mock

import installer
import winreg


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.payload = self.root / "payload"
        self.target = self.root / "LocalOpsWindows"
        self.shortcuts = (self.root / "Start.lnk", self.root / "Desktop.lnk")
        self.registry = r"Software\LocalOpsWindows.Tests" + "\\" + uuid.uuid4().hex
        self.patches = [
            mock.patch.object(installer, "INSTALL_DIR", self.target),
            mock.patch.object(installer, "_payload_dir", return_value=self.payload),
            mock.patch.object(installer, "_shortcut_paths", return_value=self.shortcuts),
            mock.patch.object(installer, "RUN_KEY", self.registry + r"\Run"),
            mock.patch.object(installer, "UNINSTALL_KEY", self.registry + r"\Uninstall"),
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
        for path in (self.registry + r"\Run", self.registry + r"\Uninstall", self.registry):
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
        data = self.root / "user-data.json"
        data.write_bytes(b"private-fixture")
        installer.install(launch=False)
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
        self.target.mkdir()
        (self.target / "fixture.txt").write_bytes(b"fixture")
        preserved = self.root / "user-data.json"
        preserved.write_bytes(b"private-fixture")
        process = installer._schedule_remove(self.target)
        self.assertEqual(process.wait(timeout=15), 0)
        self.assertFalse(self.target.exists())
        self.assertEqual(preserved.read_bytes(), b"private-fixture")


if __name__ == "__main__":
    unittest.main()
