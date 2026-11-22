# 一言 Tile 一键安装脚本
#
# 用法（管理员 PowerShell，在解压后的目录里）：
#   powershell -ExecutionPolicy Bypass -File .\install-app.ps1
#
# 证书是两层结构：
#   根证书 OneSentenceTileRoot.cer  → 本地计算机 \ 受信任的根证书颁发机构（只需一次）
#   叶证书 OneSentenceTile.cer      → 本地计算机 \ 受信任人
# 以后换版本只要根不变，就不用再装证书。

[CmdletBinding()]
param(
    [string]$Package,
    [string]$Certificate,
    [string]$RootCertificate,
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
    foreach ($ext in '*.appxbundle', '*.msixbundle', '*.appx') {
        $Package = Find-First $ext
        if ($Package) { break }
    }
}
if (-not $Package) { throw "没找到安装包（.appxbundle / .msixbundle / .appx），请把本脚本放到解压后的目录里" }

if (-not $RootCertificate) { $RootCertificate = Find-First 'OneSentenceTileRoot.cer' }
if (-not $Certificate -and $RootCertificate) {
    # 排除根证书，剩下的那个才是叶证书
    $Certificate = Get-ChildItem -Path $here -Recurse -File -Filter '*.cer' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $RootCertificate } |
        Sort-Object Length -Descending | Select-Object -First 1 |
        ForEach-Object { $_.FullName }
}
if (-not $Certificate) { $Certificate = Find-First '*.cer' }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "安装包 : $Package"
Write-Host "根证书 : $RootCertificate"
Write-Host "叶证书 : $Certificate"
Write-Host "管理员 : $isAdmin"

function Import-Cert([string]$path, [string]$store) {
    Write-Host "导入 $path → $store"
    Import-Certificate -FilePath $path -CertStoreLocation $store | Out-Null
}

if (-not $SkipCertificate) {
    if ($isAdmin) {
        if ($RootCertificate) {
            try { Import-Cert $RootCertificate 'Cert:\LocalMachine\Root'; Write-Host '根证书导入成功' -ForegroundColor Green }
            catch { Write-Warning "根证书导入失败：$($_.Exception.Message)" }
        }
        if ($Certificate) {
            try { Import-Cert $Certificate 'Cert:\LocalMachine\TrustedPeople'; Write-Host '叶证书导入成功' -ForegroundColor Green }
            catch { Write-Warning "叶证书导入失败：$($_.Exception.Message)" }
        }
    }
    else {
        Write-Warning '非管理员：只能导入当前用户存储，安装 Appx 可能需要额外信任'
        if ($Certificate) {
            try { Import-Cert $Certificate 'Cert:\CurrentUser\TrustedPeople' } catch { Write-Warning $_.Exception.Message }
        }
    }
}

$existing = Get-AppxPackage -Name '*OneSentenceTile*' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "检测到已安装：$($existing.Name) $($existing.Version)"
    Write-Host "  升级会直接覆盖；若提示签名/身份不符，先运行： Remove-AppxPackage $($existing.PackageFullName)"
}

Write-Host '正在安装应用包...'
Add-AppxPackage -Path $Package -ForceUpdateFromAnyVersion -ErrorAction Stop
Write-Host '安装完成 🎉' -ForegroundColor Green

$installed = Get-AppxPackage -Name '*OneSentenceTile*' | Select-Object -First 1
if ($installed) {
    Write-Host "已安装版本：$($installed.Version)"
}
