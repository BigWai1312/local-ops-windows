import unittest
from pathlib import Path
import tempfile
from unittest import mock

from tools import check_privacy


class PrivacyScannerTests(unittest.TestCase):
    def test_repository_is_free_of_local_identity(self):
        self.assertEqual(check_privacy.scan(), [])

    def test_example_windows_path_is_allowed(self):
        self.assertIn("example", check_privacy.PLACEHOLDER_USERS)

    def test_real_username_is_not_a_placeholder(self):
        self.assertNotIn("real-user", check_privacy.PLACEHOLDER_USERS)

    def test_csharp_source_and_project_files_are_scanned(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for suffix in (".cs", ".xaml", ".csproj", ".manifest"):
                (root / ("fixture" + suffix)).write_text(
                    "contact: private" + "@example.org", encoding="utf-8"
                )
            with mock.patch.object(check_privacy, "ROOT", root):
                failures = check_privacy.scan()
            for suffix in (".cs", ".xaml", ".csproj", ".manifest"):
                self.assertTrue(any("fixture" + suffix in failure for failure in failures))

    def test_csharp_build_outputs_are_excluded_but_other_bin_and_obj_are_scanned(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            generated = []
            source = []
            for output in ("bin", "obj"):
                generated.append(root / "csharp" / "Fixture" / output / "fixture.json")
                source.append(root / "static" / output / "fixture.json")
            for path in generated + source:
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("{}", encoding="utf-8")
            with mock.patch.object(check_privacy, "ROOT", root):
                scanned = set(check_privacy.iter_files())
            self.assertTrue(scanned.isdisjoint(generated))
            self.assertTrue(scanned.issuperset(source))


if __name__ == "__main__":
    unittest.main()
