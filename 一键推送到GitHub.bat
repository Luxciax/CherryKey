@echo off
chcp 65001 >nul
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-To-GitHub.ps1"
if errorlevel 1 (
  echo.
  echo 推送失败，请查看上方错误信息。
  pause
)
