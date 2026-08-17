#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Windows 受控进程锚点（本地运维台 Windows 专用）。

由 server.py 的 _start_app_windows 拉起：argv[1] 是本次启动的随机标记
（console-run:<token>），argv[2] 是用户在启动台保存的命令字符串。

行为类似一个持久的命令包装器：
1. 把用户命令写入临时 .cmd 批处理文件，再以 ``cmd /d /c`` 执行
   （cmd 对含引号命令行的解析规则与 POSIX 完全不同，批处理文件是
   唯一能原样执行任意命令的稳妥通道）；文件以系统区域编码写入，
   与 cmd 的解析一致；
2. 直接子进程退出后继续等到整棵进程树清空再退出（对应 bash 的 ``wait``），
   因此“脚本把服务放后台后自己退出”的场景下锚点仍是受控身份锚；
3. 以直接子进程的退出码退出，供本地运维台 Windows 记录任务成功/失败。

本地运维台 Windows 自身重启不影响本锚点：锚点独立存活，受控身份由命令行标记 +
PPID 后代树识别（Windows 子进程在父进程退出后仍保留原 PPID）。
"""

import ctypes
import locale
import os
import subprocess
import sys
import tempfile
import time
from ctypes import wintypes

CREATE_NO_WINDOW = 0x08000000
POLL_SEC = 2.0


class _PROCESSENTRY32W(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_size_t),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", wintypes.LONG),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", wintypes.WCHAR * 260),
    ]


_KERNEL32 = ctypes.WinDLL("kernel32", use_last_error=True)
_KERNEL32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE
_KERNEL32.CreateToolhelp32Snapshot.argtypes = [wintypes.DWORD, wintypes.DWORD]
_KERNEL32.Process32FirstW.restype = wintypes.BOOL
_KERNEL32.Process32FirstW.argtypes = [
    wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
_KERNEL32.Process32NextW.restype = wintypes.BOOL
_KERNEL32.Process32NextW.argtypes = [
    wintypes.HANDLE, ctypes.POINTER(_PROCESSENTRY32W)]
_KERNEL32.CloseHandle.argtypes = [wintypes.HANDLE]


def _batch_file(command):
    """写入临时 .cmd 文件，返回其路径。调用方负责删除。"""
    encoding = locale.getpreferredencoding(False) or "utf-8"
    fd, path = tempfile.mkstemp(prefix="console-", suffix=".cmd")
    with os.fdopen(fd, "w", encoding=encoding, errors="replace",
                   newline="\r\n") as f:
        f.write("@echo off\r\n")
        f.write(command + "\r\n")
        f.write("exit /b %errorlevel%\r\n")
    return path


def _live_descendants(root_pid):
    """用 Toolhelp 快照判断 root 是否仍有存活后代。"""
    snapshot = _KERNEL32.CreateToolhelp32Snapshot(0x00000002, 0)
    if not snapshot or snapshot == ctypes.c_void_p(-1).value:
        return None
    children = {}
    try:
        entry = _PROCESSENTRY32W()
        entry.dwSize = ctypes.sizeof(entry)
        more = _KERNEL32.Process32FirstW(snapshot, ctypes.byref(entry))
        while more:
            pid = int(entry.th32ProcessID)
            ppid = int(entry.th32ParentProcessID)
            if pid > 0 and ppid > 0:
                children.setdefault(ppid, []).append(pid)
            more = _KERNEL32.Process32NextW(snapshot, ctypes.byref(entry))
    finally:
        _KERNEL32.CloseHandle(snapshot)
    stack = list(children.get(root_pid, []))
    seen = set()
    while stack:
        pid = stack.pop()
        if pid in seen:
            continue
        seen.add(pid)
        stack.extend(children.get(pid, []))
        return True
    return False


def main():
    if len(sys.argv) < 3:
        return 1
    _marker, command = sys.argv[1], sys.argv[2]
    batch = _batch_file(command)
    try:
        proc = subprocess.Popen(
            ["cmd", "/d", "/c", batch],
            creationflags=CREATE_NO_WINDOW)
    except OSError:
        return 1
    try:
        code = proc.wait()
        try:
            while _live_descendants(proc.pid) is True:
                time.sleep(POLL_SEC)
        except KeyboardInterrupt:
            pass
        return code if isinstance(code, int) else 1
    finally:
        try:
            os.remove(batch)
        except OSError:
            pass


if __name__ == "__main__":
    sys.exit(main())
