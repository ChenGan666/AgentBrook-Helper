@echo off
setlocal

REM Publish AgentBrook.Helper for Windows (win-x64 / win-arm64).
REM Auto-increment version.txt patch; override via VERSION env var.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish-helper.ps1" %*
if %errorlevel% neq 0 (
    echo.
    echo Publish failed.
    pause
)
