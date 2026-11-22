@echo off
chcp 65001 >nul
setlocal

rem 一言 Tile 一键安装（免证书模式）
rem 双击运行即可；需要先在「设置 → 开发者选项」里打开「开发人员模式」

cd /d "%~dp0"

echo ============================================
echo  一言 Tile 安装（免证书 / 开发者模式）
echo ============================================
echo.

where powershell >nul 2>nul
if errorlevel 1 (
    echo 没找到 PowerShell。
    pause
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install-app.ps1" -Register

echo.
echo 如果提示需要开发者模式：设置 -^> 隐私和安全性 -^> 开发者选项 -^> 开发人员模式
pause
