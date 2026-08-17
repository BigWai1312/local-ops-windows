import hashlib
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import updater


class UpdaterTests(unittest.TestCase):
    def test_version_key_accepts_v_prefix_and_prerelease(self):
        self.assertEqual(updater.version_key("v1.2.3"), (1, 2, 3))
        self.assertEqual(updater.version_key("1.2.3-beta.1"), (1, 2, 3))
        self.assertGreater(updater.version_key("1.10.0"), updater.version_key("1.9.9"))

    def test_release_asset_urls_are_allowlisted(self):
        good = "https://github.com/BigWai1312/local-ops-windows/releases/download/v1.0.0/LocalOpsWindows-Setup.exe"
        bad_repo = "https://github.com/other/project/releases/download/v1.0.0/a.exe"
        plain_http = good.replace("https://", "http://")
        self.assertTrue(updater._allowed_asset_url(good))
        self.assertFalse(updater._allowed_asset_url(bad_repo))
        self.assertFalse(updater._allowed_asset_url(plain_http))

    def test_download_and_verify_rejects_mismatched_checksum(self):
        with tempfile.TemporaryDirectory() as td:
            release = {
                "version": "1.0.1",
                "installer": {"url": "https://example.invalid/setup.exe"},
                "checksum": {"url": "https://example.invalid/setup.exe.sha256"},
            }
            def fake_download(url, destination):
                destination.parent.mkdir(parents=True, exist_ok=True)
                if destination.suffix == ".exe":
                    destination.write_bytes(b"payload")
                else:
                    destination.write_text("0" * 64 + "  setup.exe\n", encoding="utf-8")
            with mock.patch.object(updater, "_download", side_effect=fake_download):
                with self.assertRaisesRegex(ValueError, "SHA-256"):
                    updater.download_and_verify(release, td)

    def test_download_and_verify_returns_installer_on_valid_checksum(self):
        with tempfile.TemporaryDirectory() as td:
            payload = b"payload"
            digest = hashlib.sha256(payload).hexdigest()
            release = {
                "version": "1.0.1",
                "installer": {"url": "https://example.invalid/setup.exe"},
                "checksum": {"url": "https://example.invalid/setup.exe.sha256"},
            }
            def fake_download(url, destination):
                destination.parent.mkdir(parents=True, exist_ok=True)
                if destination.suffix == ".exe":
                    destination.write_bytes(payload)
                else:
                    destination.write_text(digest + "  setup.exe\n", encoding="utf-8")
            with mock.patch.object(updater, "_download", side_effect=fake_download):
                result = updater.download_and_verify(release, td)
            self.assertEqual(result.name, updater.INSTALLER_ASSET)
            self.assertEqual(result.read_bytes(), payload)


if __name__ == "__main__":
    unittest.main()
