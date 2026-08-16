import unittest
from unittest import mock

from tools import check_privacy


class PrivacyScannerTests(unittest.TestCase):
    def test_repository_is_free_of_local_identity(self):
        self.assertEqual(check_privacy.scan(), [])

    def test_example_windows_path_is_allowed(self):
        self.assertIn("example", check_privacy.PLACEHOLDER_USERS)

    def test_real_username_is_not_a_placeholder(self):
        self.assertNotIn("real-user", check_privacy.PLACEHOLDER_USERS)


if __name__ == "__main__":
    unittest.main()
