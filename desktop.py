#!/usr/bin/env python3
"""Windows desktop shell for Local Ops Windows.

The existing HTML/CSS/JavaScript stays the product UI. This module only owns
the native window, tray lifecycle and the embedded WebView2 renderer.
"""

from __future__ import annotations

import argparse
import ctypes
import json
import os
from pathlib import Path
import threading
import time
import webbrowser
import winreg

import server


WINDOW_TITLE = "本地运维台 Windows"
WINDOW_WIDTH = 1280
WINDOW_HEIGHT = 820
WINDOW_MIN_SIZE = (980, 680)
WEBVIEW2_RUNTIME_GUIDS = (
    "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    "{2CD8A007-E189-409D-A2C8-9AF4EF3C72AA}",
    "{0D50BFEC-CD6A-4F9A-964C-C7416E3ACB10}",
    "{65C35B14-6C1D-4122-AC46-7148CC9D6497}",
)
WEBVIEW2_DOWNLOAD_URL = (
    "https://developer.microsoft.com/microsoft-edge/webview2/"
)


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description="Local Ops Windows desktop client")
    parser.add_argument("--background", action="store_true",
                        help="start hidden in the notification area")
    parser.add_argument("--browser", action="store_true",
                        help="debug mode: serve and open the external browser")
    parser.add_argument("--no-browser", action="store_true",
                        help="debug mode: serve without a desktop window")
    parser.add_argument("--preferred-port", type=int, default=None)
    parser.add_argument("--prepare-storage", action="store_true")
    parser.add_argument("--restart-helper", nargs=2, type=int, metavar=("PID", "PORT"))
    return parser.parse_known_args(argv)[0]


def _registry_version(hive, path, access):
    try:
        with winreg.OpenKey(hive, path, 0, winreg.KEY_READ | access) as key:
            value, _ = winreg.QueryValueEx(key, "pv")
            value = str(value).strip()
            return value if value and value != "0.0.0.0" else None
    except (FileNotFoundError, OSError, TypeError):
        return None


def webview2_runtime_version():
    """Return an installed WebView2 version, or ``None`` if unavailable."""
    if os.name != "nt":
        return None
    suffixes = (
        r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{}",
        r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{}",
    )
    hives = (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE)
    views = (getattr(winreg, "KEY_WOW64_64KEY", 0),
             getattr(winreg, "KEY_WOW64_32KEY", 0))
    for guid in WEBVIEW2_RUNTIME_GUIDS:
        for suffix in suffixes:
            path = suffix.format(guid)
            for hive in hives:
                for view in views:
                    version = _registry_version(hive, path, view)
                    if version:
                        return version
    return None


def focus_existing_window():
    """Bring the existing desktop client window to the foreground."""
    if os.name != "nt":
        return False
    user32 = ctypes.windll.user32
    hwnd = user32.FindWindowW(None, WINDOW_TITLE)
    if not hwnd:
        return False
    SW_RESTORE = 9
    user32.ShowWindow(hwnd, SW_RESTORE)
    user32.SetForegroundWindow(hwnd)
    return True


def _message_box(message, title=WINDOW_TITLE):
    if os.name == "nt":
        ctypes.windll.user32.MessageBoxW(0, message, title, 0x10)


class WindowState:
    """Private per-user window state; no service data is written here."""

    def __init__(self):
        self.path = Path(server.DATA_DIR) / "window.json"
        self.values = self._load()

    def _load(self):
        try:
            raw = json.loads(self.path.read_text(encoding="utf-8"))
            if not isinstance(raw, dict):
                return {}
            result = {}
            for name, low, high in (
                ("width", WINDOW_MIN_SIZE[0], 3840),
                ("height", WINDOW_MIN_SIZE[1], 2160),
            ):
                value = raw.get(name)
                if isinstance(value, int) and low <= value <= high:
                    result[name] = value
            for name in ("x", "y"):
                value = raw.get(name)
                if isinstance(value, int) and -4096 <= value <= 8192:
                    result[name] = value
            return result
        except (OSError, ValueError, TypeError):
            return {}

    def save(self, window):
        values = dict(self.values)
        for name in ("x", "y", "width", "height"):
            value = getattr(window, name, None)
            if isinstance(value, int):
                values[name] = value
        payload = json.dumps(values, ensure_ascii=True, sort_keys=True) + "\n"
        temporary = self.path.with_suffix(".tmp")
        try:
            self.path.parent.mkdir(parents=True, exist_ok=True)
            temporary.write_text(payload, encoding="utf-8")
            os.replace(temporary, self.path)
        except OSError:
            try:
                temporary.unlink()
            except OSError:
                pass


class DesktopController:
    def __init__(self, httpd, port, lock_file, background=False):
        self.httpd = httpd
        self.port = port
        self.lock_file = lock_file
        self.background = background
        self.window = None
        self.tray = None
        self.quitting = False
        self.server_thread = threading.Thread(
            target=self.httpd.serve_forever,
            name="local-ops-http",
            daemon=True,
        )
        self.state = WindowState()

    def _asset(self, name):
        return os.path.join(server.STATIC_DIR, "assets", name)

    def _load_tray_icon(self):
        from PIL import Image

        path = self._asset("console-app-icon.png")
        if os.path.isfile(path):
            return Image.open(path).convert("RGBA")
        return Image.new("RGBA", (64, 64), (35, 95, 180, 255))

    def show_window(self, _icon=None, _item=None):
        if self.window is None:
            return
        self.window.restore()
        self.window.show()
        self.background = False

    def hide_window(self, _icon=None, _item=None):
        if self.window is not None:
            self.window.hide()
        self.background = True

    def open_browser(self, _icon=None, _item=None):
        webbrowser.open("http://%s:%d/" % (server.HOST, self.port))

    def quit(self, icon=None, _item=None):
        self.quitting = True
        if icon is not None:
            try:
                icon.stop()
            except Exception:
                pass
        threading.Thread(target=self.httpd.shutdown, daemon=True).start()
        if self.window is not None:
            self.window.destroy()

    def _run_tray(self):
        import pystray

        menu = pystray.Menu(
            pystray.MenuItem("显示主窗口", self.show_window, default=True),
            pystray.MenuItem("隐藏窗口", self.hide_window),
            pystray.MenuItem("打开备用网页", self.open_browser),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("退出 Local Ops Windows", self.quit),
        )
        self.tray = pystray.Icon(
            "LocalOpsWindows", self._load_tray_icon(), WINDOW_TITLE, menu
        )
        self.tray.run()

    def _on_closing(self):
        if self.quitting:
            return True
        self.state.save(self.window)
        threading.Timer(0, self.hide_window).start()
        return False

    def _on_resized(self, width, height):
        if isinstance(width, int) and isinstance(height, int):
            self.state.values.update(width=width, height=height)

    def _on_moved(self, x, y):
        if isinstance(x, int) and isinstance(y, int):
            self.state.values.update(x=x, y=y)

    def _monitor_server(self):
        self.server_thread.join()
        if self.quitting or self.window is None:
            return
        self.quitting = True
        self.window.destroy()

    def run(self):
        server_started = False
        try:
            import webview

            values = self.state.values
            self.window = webview.create_window(
                WINDOW_TITLE,
                url="http://%s:%d/?desktop=1" % (server.HOST, self.port),
                width=values.get("width", WINDOW_WIDTH),
                height=values.get("height", WINDOW_HEIGHT),
                x=values.get("x"),
                y=values.get("y"),
                min_size=WINDOW_MIN_SIZE,
                hidden=self.background,
                resizable=True,
                text_select=True,
                zoomable=True,
                background_color="#f5f7fb",
            )
            self.window.events.closing += self._on_closing
            self.window.events.resized += self._on_resized
            self.window.events.moved += self._on_moved
            self.server_thread.start()
            server_started = True
            threading.Thread(target=self._monitor_server, daemon=True).start()

            def ready():
                threading.Thread(target=self._run_tray, daemon=True,
                                 name="local-ops-tray").start()

            webview.start(ready, gui="edgechromium", debug=False)
        finally:
            self.quitting = True
            if self.tray is not None:
                try:
                    self.tray.stop()
                except Exception:
                    pass
            if server_started and self.server_thread.is_alive():
                self.httpd.shutdown()
                self.server_thread.join(timeout=3)
            self.httpd.server_close()
            server.release_instance_lock(self.lock_file)


def run_desktop(preferred_port=None, background=False):
    if webview2_runtime_version() is None:
        _message_box(
            "未检测到 Microsoft Edge WebView2 Runtime。\n\n"
            "请先安装 WebView2 Runtime，再启动本地运维台 Windows。\n"
            + WEBVIEW2_DOWNLOAD_URL
        )
        return 1
    session = server.start_console_server(
        preferred_port, log_to_file=background
    )
    if session is None:
        focus_existing_window()
        return 0
    httpd, port, lock_file = session
    try:
        DesktopController(httpd, port, lock_file, background=background).run()
    except Exception as exc:
        _message_box("桌面客户端启动失败：%s" % exc)
        return 1
    return 0


def main(argv=None):
    args = parse_args(argv)
    if args.prepare_storage:
        server.prepare_runtime_storage()
        return 0
    if args.restart_helper:
        return server.restart_helper(*args.restart_helper)
    if args.browser:
        return 0 if server.main(args.preferred_port, open_browser=True) else 1
    if args.no_browser:
        return 0 if server.main(args.preferred_port, open_browser=False,
                                log_to_file=args.background) else 1
    return run_desktop(args.preferred_port, background=args.background)


if __name__ == "__main__":
    raise SystemExit(main())
