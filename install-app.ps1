# 一言 Tile 安装脚本
#
# 用法（在解压后的目录里）：
#   双击 安装.cmd                                        ← 会自动申请管理员权限
#   powershell -ExecutionPolicy Bypass -File .\install-app.ps1          （证书模式）
#   powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register（免证书，需开发者模式）
#
# 证书模式做的事：
#   1. 把 .cer 导入「受信任人」(TrustedPeople) —— 自签代码签名证书的正确位置
#   2. 先装 Dependencies 里的依赖框架（Microsoft.NET.CoreRuntime / VCLibs）
#   3. Add-AppxPackage 装主程序包
#
# 常见报错对照：
#   0x80073CF3 + “依赖找不到的框架”  → 依赖没装，本脚本第 2 步就是干这个的
#   “证书缺少基本约束扩展”           → 证书装错到「受信任的根」了，要用「受信任人」

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

# ---------- 找主包 ----------
if (-not $Package) {
    foreach ($ext in '*.appxbundle', '*.msixbundle', '*.appx') {
        $f = Find-One $ext
        if ($f) { $Package = $f.FullName; break }
    }
}
if (-not $Package) { throw "没找到安装包，请把本脚本放到解压后的目录里" }
$pkgDir = Split-Path -Parent $Package

# ---------- 找依赖包（Dependencies\<arch>\*.appx）----------
function Get-Dependencies([string]$dir) {
    $list = @()
    foreach ($arch in 'x64', 'x86', 'arm', 'arm64') {
        $sub = Join-Path $dir "Dependencies\$arch"
        if (Test-Path $sub) {
            $list += @(Get-ChildItem $sub -Filter '*.appx' -File -ErrorAction SilentlyContinue |
                       Select-Object -ExpandProperty FullName)
        }
    }
    if ($list.Count -eq 0) {
        $list = @(Get-ChildItem $dir -Recurse -File -Filter 'Microsoft.*.appx' -ErrorAction SilentlyContinue |
                  Select-Object -ExpandProperty FullName)
    }
    return $list
}

$deps = Get-Dependencies $pkgDir
Write-Host "安装包 : $Package"
Write-Host "依赖包 : $($deps.Count) 个"
$deps | ForEach-Object { Write-Host ("   - " + (Split-Path $_ -Leaf)) }

# ---------- 免证书模式 ----------
if ($Register) {
    Write-Host '模式：免证书（Add-AppxPackage -Register，需要开发者模式）'
    foreach ($d in $deps) {
        Write-Host "装依赖 $(Split-Path $d -Leaf) ..."
        try { Add-AppxPackage -Path $d } catch { Write-Warning $_.Exception.Message }
    }
    $work = Join-Path $env:TEMP ('ost-reg-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    Copy-Item $Package (Join-Path $work 'pkg.zip')
    Expand-Archive -Path (Join-Path $work 'pkg.zip') -DestinationPath $work -Force
    $manifest = Get-ChildItem -Path $work -Recurse -File -Filter 'AppxManifest.xml' | Select-Object -First 1
    if (-not $manifest) {
        $inner = Get-ChildItem -Path $work -Recurse -File -Filter '*.appx' | Select-Object -First 1
        if ($inner) {
            Copy-Item $inner.FullName (Join-Path $work 'inner.zip')
            Expand-Archive -Path (Join-Path $work 'inner.zip') -DestinationPath (Join-Path $work 'inner') -Force
            $manifest = Get-ChildItem -Path (Join-Path $work 'inner') -Recurse -File -Filter 'AppxManifest.xml' | Select-Object -First 1
        }
    }
    if (-not $manifest) { throw '包里没找到 AppxManifest.xml' }
    Add-AppxPackage -Register $manifest.FullName -DisableDevelopmentMode
    Write-Host '安装完成' -ForegroundColor Green
    return
}

# ---------- 证书模式 ----------
if (-not $Certificate) {
    $f = Get-ChildItem -Path $here -Recurse -File -Filter '*.cer' -ErrorAction SilentlyContinue |
        Sort-Object Length -Descending | Select-Object -First 1
    if ($f) { $Certificate = $f.FullName }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "证书   : $Certificate"
Write-Host "管理员 : $isAdmin"

if (-not $SkipCertificate -and $Certificate) {
    $store = if ($isAdmin) { 'Cert:\LocalMachine\TrustedPeople' } else { 'Cert:\CurrentUser\TrustedPeople' }
    try {
        Import-Certificate -FilePath $Certificate -CertStoreLocation $store | Out-Null
        Write-Host "证书已导入 $store" -ForegroundColor Green
    }
    catch { Write-Warning "证书导入失败：$($_.Exception.Message)" }
}

# 先装依赖框架，否则会报 0x80073CF3「找不到的框架」
foreach ($d in $deps) {
    $name = Split-Path $d -Leaf
    Write-Host "安装依赖：$name"
    try { Add-AppxPackage -Path $d -ErrorAction Stop; Write-Host "  OK" -ForegroundColor Green }
    catch {
        if ($_.Exception.Message -match '0x80073D06|higher version') { Write-Host "  已装更高版本，跳过" }
        else { Write-Warning "  $($_.Exception.Message)" }
    }
}

$existing = Get-AppxPackage -Name '*OneSentenceTile*' -ErrorAction SilentlyContinue
if ($existing) { Write-Host "已安装：$($existing.Version)，将覆盖升级" }

Write-Host '正在安装主程序包...'
try {
    Add-AppxPackage -Path $Package -ForceUpdateFromAnyVersion -ErrorAction Stop
}
catch {
    Write-Host "安装失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '可改用免证书模式（需开发者模式）：' -ForegroundColor Yellow
    Write-Host '  powershell -ExecutionPolicy Bypass -File .\install-app.ps1 -Register' -ForegroundColor Yellow
    throw
}

$installed = Get-AppxPackage -Name '*OneSentenceTile*' | Select-Object -First 1
if ($installed) { Write-Host "安装成功，版本：$($installed.Version)" -ForegroundColor Green }
