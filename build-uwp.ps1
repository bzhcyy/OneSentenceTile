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

$repoRoot = $root
$sln = Join-Path $repoRoot 'OneSentenceTile.sln'
$manifest = Join-Path $repoRoot 'OneSentenceTile\Package.appxmanifest'
$logDir = Join-Path $repoRoot 'build'
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

# ---------- 4. 证书（沿用 VS 的临时密钥做法：单张自签 Code Signing）----------
# 与原项目 addOneSecond_TemporaryKey.pfx 同类：非 CA 的代码签名证书。
#   * 非 CA + Code Signing EKU → AppX 可以签名（不会 APPX0107）
#   * 安装时放进「受信任人」(TrustedPeople) → 不会触发「缺少基本约束扩展」
# 注意：不要用 Add-AppDevPackage.ps1 装证书，它会把证书塞进「受信任的根」，
# 非 CA 证书装根就会报错。用 安装.cmd / install-app.ps1（装到受信任人）。
Step '准备签名证书'
$certDir = Join-Path $logDir 'cert'
if (-not (Test-Path $certDir)) { New-Item -ItemType Directory -Path $certDir | Out-Null }

# 文件名沿用 VS 临时密钥命名，方便以后替换
$pfx = Join-Path $certDir 'OneSentenceTile_TemporaryKey.pfx'
$cer = Join-Path $certDir 'OneSentenceTile_TemporaryKey.cer'

$manifestText = Get-Content $manifest -Raw
$publisher = [regex]::Match($manifestText, 'Publisher="([^"]+)"').Groups[1].Value
if (-not $publisher) { throw '清单里没读到 Publisher' }
$manifestVersion = [regex]::Match($manifestText, 'Version="([\d.]+)"').Groups[1].Value
Write-Host "清单版本: $manifestVersion（打包产物应是 OneSentenceTile_${manifestVersion}_*.appxbundle）"
$secure = ConvertTo-SecureString -String $CertificatePassword -Force -AsPlainText

function Read-PfxCert([string]$path) {
    return New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($path, $CertificatePassword)
}

function Test-CertOk([string]$path) {
    if (-not (Test-Path $path)) { return $false }
    try {
        $c = Read-PfxCert $path
        if ($c.Subject -ne $publisher) { Write-Host "已有证书 Subject=$($c.Subject)，与清单不一致，重签"; return $false }
        if ($c.NotAfter -lt (Get-Date).AddDays(30)) { Write-Host "已有证书快过期，重签"; return $false }
        return $true
    }
    catch { return $false }
}

if ($CertificatePath) {
    if (-not (Test-Path $CertificatePath)) { throw "指定的证书不存在：$CertificatePath" }
    $pfx = (Resolve-Path $CertificatePath).Path
    Write-Host "使用指定证书: $pfx"
}
elseif (Test-CertOk $pfx) {
    Write-Host "复用已有证书: $pfx"
}
else {
    Write-Host "生成自签证书（VS 临时密钥同款）：Subject=$publisher（5 年）"
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisher `
        -KeyExportPolicy Exportable -FriendlyName 'OneSentenceTile TemporaryKey' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(5)
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Write-Host "已生成: $pfx / $cer"
}

Write-Host "证书（装到「受信任人」，别装受信任的根）: $cer"

# ---------- 5. 打包 ----------
Step "编译打包（$Configuration / $Platform）"
Write-Host "准备 msbuild 参数…"
$appxDir = Join-Path $repoRoot 'OneSentenceTile\AppPackages\'
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
    # 不传 PackageCertificateThumbprint：改用 /f pfx 直接签名，
    # 避免 Signtool 去证书库里按 Subject/SHA1 查找时卡住或歧义。
}
if ($NoDotNetNative -or $Configuration -eq 'Debug' -or $StoreUpload) {
    $msbuildArgs += '/p:UseDotNetNativeToolchain=false'
}

Write-Host "启动 msbuild…"
Write-Host "命令行：msbuild $($msbuildArgs -join ' ')"
$sw = [Diagnostics.Stopwatch]::StartNew()

# 关键：不要用管道接 msbuild 的输出。
# PowerShell 里 `& msbuild ... | Tee-Object` 在多量输出 + stderr 交错时会死锁，
# 表现就是「卡在编译打包、一行日志都没有」。改成重定向到文件，再边跑边打印。
Remove-Item $log, "$log.err" -ErrorAction SilentlyContinue
$proc = Start-Process -FilePath $msbuild -ArgumentList $msbuildArgs -NoNewWindow -PassThru `
    -RedirectStandardOutput $log -RedirectStandardError "$log.err"

$offset = 0
$deadline = (Get-Date).AddMinutes(25)
while (-not $proc.HasExited) {
    Start-Sleep -Seconds 8
    if (Test-Path $log) {
        $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
        if ($lines.Count -gt $offset) {
            $lines[$offset..($lines.Count - 1)] | ForEach-Object { Write-Host $_ }
            $offset = $lines.Count
        }
    }
    if ((Get-Date) -gt $deadline) {
        Write-Warning '编译超过 25 分钟，强制终止'
        try { $proc.Kill() } catch { }
        if ($env:GITHUB_STEP_SUMMARY) {
            '### 编译卡住，最后 40 行日志' | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8
            '```' | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8
            Get-Content $log -Tail 40 -ErrorAction SilentlyContinue | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8
            '```' | Add-Content $env:GITHUB_STEP_SUMMARY -Encoding utf8
        }
        throw "编译超时（25 分钟），日志：$log"
    }
}
$proc.WaitForExit()
$sw.Stop()
$code = $proc.ExitCode

# Start-Process -PassThru 在某些情况下拿不到 ExitCode（返回 $null），
# 这时用「日志里有没有 : error」+「有没有产出 appxbundle」来判断。
if ($null -eq $code -or "$code" -eq '') {
    $hasError = Select-String -Path $log -Pattern ': error' -Quiet -ErrorAction SilentlyContinue
    $hasBundle = @(Get-ChildItem $appxDir -Recurse -File -Filter '*.appxbundle' -ErrorAction SilentlyContinue).Count -gt 0
    if ($hasBundle -and -not $hasError) { $code = 0 } else { $code = 1 }
    Write-Host "（拿不到退出码，按日志/产物判断：$code，appxbundle=$hasBundle，error=$hasError）"
}

# 把没打印完的部分补上
if (Test-Path $log) {
    $lines = @(Get-Content $log -ErrorAction SilentlyContinue)
    if ($lines.Count -gt $offset) { $lines[$offset..($lines.Count - 1)] | ForEach-Object { Write-Host $_ } }
}
if (Test-Path "$log.err") { Get-Content "$log.err" -ErrorAction SilentlyContinue | ForEach-Object { Write-Host $_ } }
Write-Host ("编译耗时: {0:N1}s，退出码: {1}" -f $sw.Elapsed.TotalSeconds, $code)

if ($code -ne 0) {
    $bundleNow = @(Get-ChildItem $appxDir -Recurse -File -Filter '*.appxbundle' -ErrorAction SilentlyContinue)
    $errNow = @(Select-String -Path $log -Pattern ': error' -ErrorAction SilentlyContinue)
    if ($bundleNow.Count -gt 0 -and $errNow.Count -eq 0) {
        Write-Host "已产出 $($bundleNow.Count) 个 appxbundle 且日志无 error，按成功处理"
        $code = 0
    }
}
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
    Write-Host '安装：解压后双击 安装.cmd（自动装证书 + 依赖框架 + 主程序）。' -ForegroundColor Green
    if (Test-Path $cer) { Write-Host "证书：$cer（手动装：Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\TrustedPeople）" }
}

# ---------- 7. CI Summary ----------
if ($env:GITHUB_STEP_SUMMARY) {
    $lines = @()
    $lines += '## 一言 Tile 构建结果'
    $lines += ''
    $lines += "| 项 | 值 |"
    $lines += "|---|---|"
    $lines += "| 版本 | ``$manifestVersion`` |"
    $lines += "| 配置 | $Configuration / $Platform |"
    $lines += "| 签名证书 | ``$publisher``（$(if (Test-Path $cer) { '已生成' } else { '复用缓存' })） |"
    $lines += "| 安装包 | $(if ($pkgs.Count -gt 0) { ($pkgs | ForEach-Object { $_.Name }) -join '<br>' } else { '未生成' }) |"
    $lines += ''
    if ($pkgs.Count -gt 0) {
        $lines += '### 安装'
        $lines += ''
        $lines += '1. 下载下方 artifact ``uwp-package`` 并解压'
        $lines += '2. 双击 ``安装.cmd``（自动提权 → 装证书到受信任人 → 装依赖 → 装主程序）'
        $lines += '3. 打开 App，设置里确认「动态磁贴」已开'
        $lines += ''
        $lines += '> 报错对照：``0x80073CF3`` = 依赖没装；「缺少基本约束扩展」= 证书装到了受信任的根。'
    }
    $lines | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8
}

if ($Register) {
    Step '注册到本机（开发者模式）'
    $appxManifest = @(Get-ChildItem (Join-Path $repoRoot "OneSentenceTile\bin\$Platform\$Configuration") -Recurse -Filter 'AppxManifest.xml' -ErrorAction SilentlyContinue |
        Select-Object -First 1)
    if (-not $appxManifest) { throw '找不到编译输出的 AppxManifest.xml，无法注册。' }
    Add-AppxPackage -Register $appxManifest.FullName
    Write-Host "已注册：$($appxManifest.FullName)" -ForegroundColor Green
}
