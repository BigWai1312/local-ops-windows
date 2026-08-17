@echo off
rem Local Ops Windows launcher: prefer py, then fall back to python.
setlocal
cd /d "%~dp0"
title Local Ops Windows
where py >nul 2>nul
if %errorlevel%==0 (
  py -3 server.py --launcher %*
) else (
  python server.py --launcher %*
)
