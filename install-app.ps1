# 一言 Tile 一键安装脚本
#
# 用法（管理员 PowerShell，在解压后的目录里）：
#   powershell -ExecutionPolicy Bypass -File .\install-app.ps1
#
# 它做三件事：
#   1. 找到同目录（或 AppPackages 下）的 .cer，导入「受信任人」(TrustedPeople)
#      —— 注意不是「受信任的根证书颁发机构」，装错地方会报「证书缺少基本约束扩展」
#   2. 找到 .appxbundle / .msixbundle，用 Add-AppxPackage 安装
#   3. 如果已装旧版本，会先提示卸载

[CmdletBinding()]
param(
    [string]$Package,
    [string]$Certificate,
    [switch]$SkipCertificate
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Find-First([string]$pattern) {
    $file = Get-ChildItem -Path $here -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending | Select-Object -First 1
    if ($file) { return $file.FullName }
    return $null
}

if (-not $Package) {
    $Package = Find-First '*.appxbundle'
    if (-not $Package) { $Package = Find-First '*.msixbundle' }
}
if (-not $Package) { $Package = Find-First '*.appx' }
if (-not $Package) { throw "没找到安装包（.appxbundle / .msixbundle / .appx），请把本脚本放到解压后的目录里" }

if (-not $Certificate) { $Certificate = Find-First '*.cer' }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "安装包 : $Package"
Write-Host "证书   : $Certificate"

if ($Certificate -and -not $SkipCertificate) {
    if (-not $isAdmin) {
        Write-Warning '导入「受信任人」需要管理员权限，将尝试用当前用户存储（可能仍需信任确认）'
        $store = 'Cert:\CurrentUser\TrustedPeople'
    }
    else {
        $store = 'Cert:\LocalMachine\TrustedPeople'
    }

    Write-Host "导入证书到 $store ..."
    try {
        Import-Certificate -FilePath $Certificate -CertStoreLocation $store | Out-Null
        Write-Host '证书导入成功' -ForegroundColor Green
    }
    catch {
        Write-Warning "导入失败：$($_.Exception.Message)"
        Write-Host '手动导入方式：双击 .cer → 安装证书 → 本地计算机 → 「受信任人」' -ForegroundColor Yellow
    }
}

# 已装旧版本？先提示
$existing = Get-AppxPackage -Name '*OneSentenceTile*' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "检测到已安装：$($existing.Name) $($existing.Version)"
    Write-Host "  升级会直接覆盖；如果提示签名/身份不符，先运行： Remove-AppxPackage $($existing.PackageFullName)"
}

Write-Host '正在安装应用包...'
Add-AppxPackage -Path $Package -ForceUpdateFromAnyVersion -ErrorAction Stop
Write-Host '安装完成 🎉' -ForegroundColor Green

$installed = Get-AppxPackage -Name '*OneSentenceTile*' | Select-Object -First 1
if ($installed) {
    Write-Host "已安装版本：$($installed.Version)"
    Write-Host "启动方式：开始菜单搜索「一言」"
}
