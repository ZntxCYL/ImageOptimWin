@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo 正在生成内置全部必要压缩引擎的 Windows x64 发布包...
echo 首次构建需要网络，并且电脑需已安装 .NET 10 SDK。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" -Runtime win-x64
if errorlevel 1 (
  echo.
  echo 构建失败。请检查网络、.NET 10 SDK，以及输出的错误信息。
  pause
  exit /b 1
)
echo.
echo 发布包位于 dist\ImageOptimWin-win-x64-Bundled.zip
pause
