import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import desktop


@unittest.skipIf(desktop.os.name != "nt", "Windows desktop client only")
class DesktopClientTests(unittest.TestCase):
    def test_default_entry_is_desktop_not_external_browser(self):
        with mock.patch.object(desktop, "run_desktop", return_value=0) as run, \
                mock.patch.object(desktop.server, "main") as server_main:
            self.assertEqual(desktop.main([]), 0)
        run.assert_called_once_with(None, background=False)
        server_main.assert_not_called()

    def test_background_entry_keeps_desktop_shell_hidden(self):
        with mock.patch.object(desktop, "run_desktop", return_value=0) as run:
            self.assertEqual(desktop.main(["--background"]), 0)
        run.assert_called_once_with(None, background=True)

    def test_browser_mode_is_explicit(self):
        with mock.patch.object(desktop.server, "main", return_value=True) as main:
            self.assertEqual(
                desktop.main(["--browser", "--preferred-port", "9603"]), 0
            )
        main.assert_called_once_with(9603, open_browser=True)

    def test_missing_webview2_fails_before_server_start(self):
        with mock.patch.object(desktop, "webview2_runtime_version", return_value=None), \
                mock.patch.object(desktop, "_message_box") as message, \
                mock.patch.object(desktop.server, "start_console_server") as start:
            self.assertEqual(desktop.run_desktop(), 1)
        start.assert_not_called()
        self.assertIn("WebView2 Runtime", message.call_args.args[0])

    def test_second_launch_focuses_existing_window(self):
        with mock.patch.object(desktop, "webview2_runtime_version", return_value="1"), \
                mock.patch.object(desktop.server, "start_console_server", return_value=None), \
                mock.patch.object(desktop, "focus_existing_window") as focus:
            self.assertEqual(desktop.run_desktop(), 0)
        focus.assert_called_once_with()

    def test_window_state_rejects_unreasonable_dimensions(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "window.json"
            path.write_text(json.dumps({
                "width": 100,
                "height": 99999,
                "x": 120,
                "y": 80,
            }), encoding="utf-8")
            with mock.patch.object(desktop.server, "DATA_DIR", directory):
                state = desktop.WindowState()
        self.assertEqual(state.values, {"x": 120, "y": 80})

    def test_window_close_hides_to_tray_until_explicit_exit(self):
        controller = object.__new__(desktop.DesktopController)
        controller.quitting = False
        controller.window = mock.Mock()
        controller.state = mock.Mock()

        with mock.patch.object(desktop.threading, "Timer") as timer:
            self.assertFalse(controller._on_closing())

        controller.state.save.assert_called_once_with(controller.window)
        timer.assert_called_once_with(0, controller.hide_window)
        timer.return_value.start.assert_called_once_with()

        controller.quitting = True
        self.assertTrue(controller._on_closing())

    def test_console_listener_does_not_share_a_windows_port(self):
        self.assertFalse(desktop.server.ConsoleServer.allow_reuse_address)


if __name__ == "__main__":
    unittest.main()
