# 一言 Tile 安装脚本
#
# 方式一（推荐，一键装完）：
#   双击 安装.cmd          ← 会自动请求管理员权限
#   或：powershell -ExecutionPolicy Bypass -File .\install-app.ps1
#
# 它做三件事：
#   1. 根证书 OneSentenceTileRoot.cer  → 本地计算机 \ 受信任的根证书颁发机构（只需一次）
#   2. 叶证书 OneSentenceTile.cer      → 本地计算机 \ 受信任人
#   3. Add-AppxPackage 安装 appxbundle
#
# 方式二（免证书，需要开发者模式）：
#   powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register

[CmdletBinding()]
param(
    [string]$Package,
    [string]$Certificate,
    [switch]$Register,
    [switch]$SkipCertificate
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Find-One([string]$pattern) {
    Get-ChildItem -Path $here -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending | Select-Object -First 1
}

if (-not $Package) {
    foreach ($ext in '*.appxbundle', '*.msixbundle', '*.appx') {
        $f = Find-One $ext
        if ($f) { $Package = $f.FullName; break }
    }
}
if (-not $Package) { throw "没找到安装包（.appxbundle / .msixbundle / .appx），请把本脚本放到解压后的目录里" }

# ---------------- 免证书模式 ----------------
if ($Register) {
    Write-Host '模式：免证书（Add-AppxPackage -Register，需要开发者模式）'
    $work = Join-Path $env:TEMP ('ost-reg-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $zipCopy = Join-Path $work 'pkg.zip'
    Copy-Item $Package $zipCopy
    Expand-Archive -Path $zipCopy -DestinationPath $work -Force
    $manifest = Get-ChildItem -Path $work -Recurse -File -Filter 'AppxManifest.xml' | Select-Object -First 1
    if (-not $manifest) {
        $inner = Get-ChildItem -Path $work -Recurse -File -Filter '*.appx' | Select-Object -First 1
        if ($inner) {
            $iz = Join-Path $work 'inner.zip'
            Copy-Item $inner.FullName $iz
            Expand-Archive -Path $iz -DestinationPath (Join-Path $work 'inner') -Force
            $manifest = Get-ChildItem -Path (Join-Path $work 'inner') -Recurse -File -Filter 'AppxManifest.xml' | Select-Object -First 1
        }
    }
    if (-not $manifest) { throw '包里没找到 AppxManifest.xml' }
    Add-AppxPackage -Register $manifest.FullName -DisableDevelopmentMode
    Write-Host '安装完成（若提示需要开发者模式：设置 → 开发者选项 → 开发人员模式）' -ForegroundColor Green
    return
}

# ---------------- 证书模式 ----------------
if (-not $Certificate) {
    $f = Get-ChildItem -Path $here -Recurse -File -Filter '*.cer' -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending | Select-Object -First 1
    if ($f) { $Certificate = $f.FullName }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "安装包 : $Package"
Write-Host "证书   : $Certificate"
Write-Host "管理员 : $isAdmin"

if (-not $SkipCertificate -and $Certificate) {
    # 关键：装「受信任人」，不是「受信任的根」。
    # 这是自签代码签名证书的正确位置；装到根会报「证书缺少基本约束扩展」。
    $store = if ($isAdmin) { 'Cert:\LocalMachine\TrustedPeople' } else { 'Cert:\CurrentUser\TrustedPeople' }
    Write-Host "导入证书到 $store ..."
    try {
        Import-Certificate -FilePath $Certificate -CertStoreLocation $store | Out-Null
        Write-Host '证书导入成功（受信任人）' -ForegroundColor Green
    }
    catch {
        Write-Warning "导入失败：$($_.Exception.Message)"
    }
}

$existing = Get-AppxPackage -Name '*OneSentenceTile*' -ErrorAction SilentlyContinue
if ($existing) { Write-Host "已安装：$($existing.Name) $($existing.Version)（升级会覆盖）" }

Write-Host '正在安装应用包...'
try {
    Add-AppxPackage -Path $Package -ForceUpdateFromAnyVersion -ErrorAction Stop
}
catch {
    Write-Host "安装失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '改用免证书模式：powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register' -ForegroundColor Yellow
    throw
}

$installed = Get-AppxPackage -Name '*OneSentenceTile*' | Select-Object -First 1
if ($installed) { Write-Host "已安装版本：$($installed.Version)" -ForegroundColor Green }
