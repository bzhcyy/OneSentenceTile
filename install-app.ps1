# 一言 Tile 安装脚本（两种模式）
#
# 模式 1（默认，需要证书）：
#   powershell -ExecutionPolicy Bypass -File .\install-app.ps1
#   → 根证书 → 受信任的根；叶证书 → 受信任人；然后 Add-AppxPackage
#
# 模式 2（免证书，需要开发者模式）：
#   powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register
#   → 把 appxbundle 解开，直接 Add-AppxPackage -Register 松散文件
#     完全不需要证书，也不会报「证书缺少基本约束扩展」

[CmdletBinding()]
param(
    [string]$Package,
    [string]$RootCertificate,
    [string]$Certificate,
    [switch]$Register,          # 免证书模式（开发者模式）
    [switch]$SkipCertificate
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Find-One([string]$pattern, [string[]]$exclude) {
    Get-ChildItem -Path $here -Recurse -File -Filter $pattern -ErrorAction SilentlyContinue |
        Where-Object { $exclude -notcontains $_.FullName } |
        Sort-Object Length -Descending | Select-Object -First 1
}

if (-not $Package) {
    foreach ($ext in '*.appxbundle', '*.msixbundle', '*.appx') {
        $f = Find-One $ext @()
        if ($f) { $Package = $f.FullName; break }
    }
}
if (-not $Package) { throw "没找到安装包（.appxbundle / .msixbundle / .appx），请把本脚本放到解压后的目录里" }

# ---------------- 模式 2：免证书注册 ----------------
if ($Register) {
    Write-Host '模式：免证书（Add-AppxPackage -Register）'
    $work = Join-Path $env:TEMP ('ost-register-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    $zipCopy = Join-Path $work 'pkg.zip'
    Copy-Item $Package $zipCopy

    Write-Host "解包到 $work ..."
    Expand-Archive -Path $zipCopy -DestinationPath $work -Force

    $manifest = Get-ChildItem -Path $work -Recurse -File -Filter 'AppxManifest.xml' |
        Sort-Object Length -Descending | Select-Object -First 1
    if (-not $manifest) {
        # bundle 里是多个 .appx，需要再解一层
        $inner = Get-ChildItem -Path $work -Recurse -File -Filter '*.appx' |
            Sort-Object Length -Descending | Select-Object -First 1
        if ($inner) {
            $innerZip = Join-Path $work 'inner.zip'
            Copy-Item $inner.FullName $innerZip
            Expand-Archive -Path $innerZip -DestinationPath (Join-Path $work 'inner') -Force
            $manifest = Get-ChildItem -Path (Join-Path $work 'inner') -Recurse -File -Filter 'AppxManifest.xml' |
                Select-Object -First 1
        }
    }
    if (-not $manifest) { throw '包里没找到 AppxManifest.xml' }

    Write-Host "注册：$($manifest.FullName)"
    Add-AppxPackage -Register $manifest.FullName -DisableDevelopmentMode
    Write-Host '安装完成 🎉（若提示需要开发者模式：设置 → 隐私和安全性 → 开发者选项 → 开发人员模式）' -ForegroundColor Green
    return
}

# ---------------- 模式 1：证书 + 安装包 ----------------
if (-not $RootCertificate) {
    $f = Find-One 'OneSentenceTileRoot.cer' @()
    if ($f) { $RootCertificate = $f.FullName }
}
if (-not $Certificate) {
    $exclude = @()
    if ($RootCertificate) { $exclude += $RootCertificate }
    $f = Find-One '*.cer' $exclude
    if ($f) { $Certificate = $f.FullName }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "安装包 : $Package"
Write-Host "根证书 : $RootCertificate"
Write-Host "叶证书 : $Certificate"
Write-Host "管理员 : $isAdmin"

if (-not $SkipCertificate) {
    if (-not $isAdmin) {
        Write-Warning '需要管理员权限才能写入「本地计算机」证书存储。请右键 PowerShell → 以管理员身份运行，再执行本脚本。'
    }
    else {
        if ($RootCertificate) {
            $root = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($RootCertificate)
            $bc = $root.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.19' }
            if (-not $bc) {
                Write-Warning '这张 .cer 没有 Basic Constraints，不能当根证书用 —— 请确认下载的是 OneSentenceTileRoot.cer'
            }
            try {
                Import-Certificate -FilePath $RootCertificate -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
                Write-Host '根证书 → 受信任的根  OK' -ForegroundColor Green
            }
            catch { Write-Warning "根证书导入失败：$($_.Exception.Message)" }
        }
        if ($Certificate) {
            try {
                Import-Certificate -FilePath $Certificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
                Write-Host '叶证书 → 受信任人  OK' -ForegroundColor Green
            }
            catch { Write-Warning "叶证书导入失败：$($_.Exception.Message)" }
        }
    }
}

$existing = Get-AppxPackage -Name '*OneSentenceTile*' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "已安装：$($existing.Name) $($existing.Version)（升级会覆盖）"
}

Write-Host '正在安装应用包...'
try {
    Add-AppxPackage -Path $Package -ForceUpdateFromAnyVersion -ErrorAction Stop
}
catch {
    Write-Host ''
    Write-Host "安装失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '可以改用免证书模式（需要先打开「开发人员模式」）：' -ForegroundColor Yellow
    Write-Host '  powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register' -ForegroundColor Yellow
    throw
}

$installed = Get-AppxPackage -Name '*OneSentenceTile*' | Select-Object -First 1
if ($installed) { Write-Host "已安装版本：$($installed.Version)" -ForegroundColor Green }
