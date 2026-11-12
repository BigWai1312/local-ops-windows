@echo off
rem Build and start the native WPF client; the backend uses Python from PATH.
setlocal
cd /d "%~dp0"
title Local Ops Windows
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK is required for source development.
  echo Use LocalOpsWindows-Setup.exe for a self-contained installation.
  exit /b 1
)
dotnet run --project "csharp\LocalOpsWindows.Client\LocalOpsWindows.Client.csproj" -c Release -- %*
