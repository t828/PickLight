@echo off
rem PickLight 一键编译：调用 build.ps1（图标自动从源码 base64 生成，无需根目录 ico）
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
echo.
pause
