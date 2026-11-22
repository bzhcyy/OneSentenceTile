@echo off
chcp 65001 >nul
setlocal

rem 一言 Tile 一键安装（自动请求管理员权限）
rem 双击运行即可。会装根证书 → 受信任的根，再装应用包。

:: 检查管理员权限，没有就重新以管理员运行自己
net session >nul 2>&1
if errorlevel 1 (
    echo 正在请求管理员权限...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

cd /d "%~dp0"

echo ============================================
echo  一言 Tile 安装
echo ============================================
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-app.ps1"
set RC=%ERRORLEVEL%

echo.
if not "%RC%"=="0" (
    echo 安装失败，退出码 %RC%
    echo 可以试试免证书模式：需要先在「设置 → 开发者选项」里打开「开发人员模式」
    echo   powershell -ExecutionPolicy Bypass -File "%~dp0install-app.ps1" -Register
)
pause
exit /b %RC%
