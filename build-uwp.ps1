# 打包 UWP（add-one-second）
#
# 用法（仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\build-uwp.ps1
#   powershell -ExecutionPolicy Bypass -File .\build-uwp.ps1 -Configuration Release -NoDotNetNative
#
# 它做的事：
#   1. 用 vswhere 找 MSBuild（不写死 VS 安装路径）
#   2. 自动挑一个已装的 Windows 10 SDK（优先 19041）
#   3. 没装 「Windows Mobile Extensions」 时自动 /p:IncludeMobileSdk=false（源码里已用 #if MOBILE_SDK 兜底）
#   4. 缺证书时按清单里的 Publisher 生成同名自签证书并导出 pfx/cer（复用 build\cert 里的旧证书）
#   5. 还原 + 编译 + 打包（默认 Release / x64 / .appxbundle）
#   6. 失败时把日志里前 15 条 : error 打出来，完整日志在 build\msbuild.log
#
# 常用参数：
#   -Configuration Debug|Release     默认 Release
#   -Platform x86|x64|ARM            默认 x64
#   -NoDotNetNative                  关 .NET Native（缺工具链时必加）
#   -NoMobileSdk / -ForceMobileSdk   强制不引用/强制引用 Windows Mobile 扩展
#   -TargetPlatformVersion 10.0.26100.0
#   -StoreUpload                     生成商店上传包（不签名）
#   -Register                        打完后 Add-AppxPackage -Register 直接注册到本机（需开发者模式）

[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x86', 'x64', 'ARM')]
    [string]$Platform = 'x64',

    [switch]$NoDotNetNative,
    [switch]$NoMobileSdk,
    [switch]$ForceMobileSdk,
    [switch]$StoreUpload,
    [switch]$Register,

    [string]$TargetPlatformVersion,
    [string]$CertificatePath,
    [string]$CertificatePassword = 'OneSentenceTile'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $root) { $root = (Get-Location).Path }

$sln = Join-Path $root 'OneSentenceTile.sln'
$manifest = Join-Path $root 'OneSentenceTile\Package.appxmanifest'
$logDir = Join-Path $root 'build'
$log = Join-Path $logDir 'msbuild.log'

function Step([string]$msg) { Write-Host ''; Write-Host "== $msg" -ForegroundColor Cyan }

if (-not (Test-Path $sln)) { throw "找不到 $sln（请在仓库根目录运行本脚本）" }
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

# ---------- 1. MSBuild ----------
Step '定位 MSBuild'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "找不到 vswhere：$vswhere。请先安装 Visual Studio 2019/2022（含「通用 Windows 平台开发」工作负载）。"
}
$msbuild = (& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1)
if (-not $msbuild) {
    $msbuild = (& $vswhere -latest -products * -property installationPath | ForEach-Object {
            Join-Path $_ 'MSBuild\Current\Bin\MSBuild.exe'
        } | Where-Object { Test-Path $_ } | Select-Object -First 1)
}
if (-not $msbuild) { throw '找不到 MSBuild.exe，请确认 Visual Studio 装了 MSBuild 组件。' }
Write-Host "MSBuild: $msbuild"
try { Write-Host ("MSBuild 版本: " + ((& $msbuild -version -nologo) -join ' ')) } catch { }

$uwpWorkload = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Workload.Universal -property installationPath)
if (-not $uwpWorkload) { Write-Warning '没检测到「通用 Windows 平台开发」工作负载，微软的 XAML 编译目标可能缺失。' }

# ---------- 2. Windows 10 SDK ----------
Step '选择 Windows 10 SDK'
$kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10'
$sdks = @(Get-ChildItem (Join-Path $kitsRoot 'Include') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^10\.0\.\d+\.0$' } | Select-Object -ExpandProperty Name | Sort-Object -Descending)
if (-not $sdks) { throw "没装 Windows 10 SDK（$kitsRoot\Include 下没有 10.0.*）。" }

if ($TargetPlatformVersion) {
    if ($sdks -notcontains $TargetPlatformVersion) { throw "指定的 SDK $TargetPlatformVersion 未安装，已装：$($sdks -join ', ')" }
    $tpv = $TargetPlatformVersion
}
elseif ($sdks -contains '10.0.19041.0') { $tpv = '10.0.19041.0' }
else { $tpv = $sdks[0] }
Write-Host "TargetPlatformVersion: $tpv（已装：$($sdks -join ', ')）"

# ---------- 3. Windows Mobile 扩展 ----------
Step '检查 Windows Mobile 扩展'
$mobileRoot = Join-Path $kitsRoot 'Extension SDKs\WindowsMobile'
$mobileFound = (Test-Path $mobileRoot) -and (@(Get-ChildItem $mobileRoot -Directory -ErrorAction SilentlyContinue).Count -gt 0)
$includeMobile = $mobileFound
if ($NoMobileSdk) { $includeMobile = $false }
if ($ForceMobileSdk) { $includeMobile = $true }
Write-Host ("Windows Mobile Extensions: " + $(if ($mobileFound) { '已安装' } else { '未安装' }) + "；本次 IncludeMobileSdk=$($includeMobile.ToString().ToLower())")
if ($includeMobile -and -not $mobileFound) { Write-Warning '强制引用 Windows Mobile 扩展，但它没装，编译大概率报 SDKReference 找不到。' }

# ---------- 4. 证书 ----------
Step '准备签名证书'
$certDir = Join-Path $logDir 'cert'
if (-not (Test-Path $certDir)) { New-Item -ItemType Directory -Path $certDir | Out-Null }
$pfx = Join-Path $certDir 'OneSentenceTile.pfx'
$cer = Join-Path $certDir 'OneSentenceTile.cer'

# 清单里的 Subject（改过 Publisher 就得换证书，否则签名报 APPX0108）
$publisher = [regex]::Match((Get-Content $manifest -Raw), 'Publisher="([^"]+)"').Groups[1].Value
if (-not $publisher) { throw '清单里没读到 Publisher' }

if ($CertificatePath) {
    if (-not (Test-Path $CertificatePath)) { throw "指定的证书不存在：$CertificatePath" }
    $pfx = (Resolve-Path $CertificatePath).Path
    Write-Host "使用指定证书: $pfx"
}
elseif (Test-Path $pfx) {
    # 旧证书的 Subject 可能和清单对不上（比如 Publisher 改过），对不上就重签
    $subjectOk = $false
    try {
        $existing = Get-PfxCertificate -FilePath $pfx
        $subjectOk = ($existing.Subject -eq $publisher)
        Write-Host ("已有证书 Subject=" + $existing.Subject + "（清单要求 " + $publisher + "）")
    }
    catch { Write-Warning "读取已有证书失败，将重新生成" }
    if (-not $subjectOk) {
        Write-Warning "证书与清单 Publisher 不一致，删掉重建"
        Remove-Item $pfx -Force -ErrorAction SilentlyContinue
        Remove-Item $cer -Force -ErrorAction SilentlyContinue
    }
}
if (-not (Test-Path $pfx)) {
    Write-Host "生成自签证书，Subject=$publisher"
    $secure = ConvertTo-SecureString -String $CertificatePassword -Force -AsPlainText
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName 'OneSentenceTile dev' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(5)
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host "已生成: $pfx / $cer"
}

# ---------- 5. 打包 ----------
Step "编译打包（$Configuration / $Platform）"
$appxDir = Join-Path $root 'OneSentenceTile\AppPackages\'
# MSBuild 命令行里的路径统一用正斜杠：反斜杠结尾会转义掉收尾引号，导致 /p 参数解析错乱
$appxDirArg = ($appxDir -replace '\\', '/')
$pfxArg = ($pfx -replace '\\', '/')
$slnArg = ($sln -replace '\\', '/')
$msbuildArgs = @(
    $slnArg
    '/restore'
    '/t:Build'
    '/nologo'
    '/v:m'
    "/p:Configuration=$Configuration"
    "/p:Platform=$Platform"
    "/p:TargetPlatformVersion=$tpv"
    "/p:IncludeMobileSdk=$($includeMobile.ToString().ToLower())"
    "/p:AppxPackageDir=$appxDirArg"
    '/p:AppxAutoIncrementPackageRevision=false'
)
if ($StoreUpload) {
    $msbuildArgs += '/p:UapAppxPackageBuildMode=StoreUpload'
    $msbuildArgs += '/p:AppxPackageSigningEnabled=false'
    Write-Host '模式: StoreUpload（不签名，产物是 .msixupload/.appxupload）'
}
else {
    $msbuildArgs += '/p:UapAppxPackageBuildMode=SideloadOnly'
    $msbuildArgs += '/p:AppxBundle=Always'
    $msbuildArgs += "/p:AppxBundlePlatforms=$Platform"
    $msbuildArgs += '/p:AppxPackageSigningEnabled=true'
    $msbuildArgs += "/p:PackageCertificateKeyFile=$pfxArg"
    $msbuildArgs += "/p:PackageCertificatePassword=$CertificatePassword"
}
if ($NoDotNetNative -or $Configuration -eq 'Debug' -or $StoreUpload) {
    $msbuildArgs += '/p:UseDotNetNativeToolchain=false'
}

Write-Host "命令行：msbuild $($msbuildArgs -join ' ')"
$sw = [Diagnostics.Stopwatch]::StartNew()
& $msbuild @msbuildArgs 2>&1 | Tee-Object -FilePath $log
$code = $LASTEXITCODE
$sw.Stop()
Write-Host ("编译耗时: {0:N1}s，退出码: {1}" -f $sw.Elapsed.TotalSeconds, $code)

if ($code -ne 0) {
    Write-Host ''
    Write-Host '--- 日志里的前 15 条错误 ---' -ForegroundColor Yellow
    $errs = @(Select-String -Path $log -Pattern ': error' -ErrorAction SilentlyContinue | Select-Object -First 15)
    $errs | ForEach-Object { Write-Host $_.Line }
    # CI 里把错误贴到 Job Summary，省得还要下载日志
    if ($env:GITHUB_STEP_SUMMARY) {
        '### 编译失败：前 15 条错误' | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
        '```' | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
        $errs | ForEach-Object { $_.Line } | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
        '```' | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
    }
    throw "编译失败（退出码 $code），完整日志：$log"
}

# ---------- 6. 产物 ----------
Step '产物'
$exts = @('.appxbundle', '.msixbundle', '.appx', '.msix', '.msixupload', '.appxupload')
$pkgs = @(Get-ChildItem $appxDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $exts -contains $_.Extension })
if ($pkgs.Count -eq 0) {
    Write-Warning "没找到安装包，检查 $appxDir（或上面日志）"
}
else {
    $pkgs | ForEach-Object { Write-Host ("  " + $_.FullName + "  (" + [math]::Round($_.Length / 1MB, 2) + " MB)") }
    Write-Host ''
    Write-Host '安装：解压后右键 Add-AppDevPackage.ps1 → 使用 PowerShell 运行（会自动装证书再装包）。' -ForegroundColor Green
    if (Test-Path $cer) { Write-Host "证书：$cer（需要时：Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople）" }
}

if ($Register) {
    Step '注册到本机（开发者模式）'
    $appxManifest = @(Get-ChildItem (Join-Path $root "OneSentenceTile\bin\$Platform\$Configuration") -Recurse -Filter 'AppxManifest.xml' -ErrorAction SilentlyContinue |
        Select-Object -First 1)
    if (-not $appxManifest) { throw '找不到编译输出的 AppxManifest.xml，无法注册。' }
    Add-AppxPackage -Register $appxManifest.FullName
    Write-Host "已注册：$($appxManifest.FullName)" -ForegroundColor Green
}
