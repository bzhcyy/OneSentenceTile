# 把磁贴服务器地址一次性写进 UWP 工程（manifest + MainPage.xaml.cs + tile/config.json）
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\set-tile-url.ps1 -BaseUrl https://<用户名>.github.io/<仓库名>
# 加 -Verify 会在改完后立刻拉一次 https://.../tile.xml 检查是不是真的磁贴 XML。
# 国内网络建议加 -Proxy http://127.0.0.1:7897。
#
# 说明：
#   - BaseUrl 只写到 Pages 根（不要带 /tile.xml），脚本自己拼 /tile.xml
#   - 幂等：重复运行只会把地址替换成新地址
#   - 会保留文件原有的 BOM / 编码，避免中文注释乱码

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$BaseUrl,

    [string]$RepoRoot,

    [string]$Proxy,

    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

function Read-Utf8Text([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
    return [pscustomobject]@{ Text = $text; HasBom = $hasBom }
}

function Write-Utf8Text([string]$Path, [string]$Text, [bool]$Bom) {
    $enc = New-Object System.Text.UTF8Encoding($Bom)
    [System.IO.File]::WriteAllText($Path, $Text, $enc)
}

# ---- 规范化地址 ----
if (-not $RepoRoot) {
    $RepoRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
}
$BaseUrl = $BaseUrl.Trim().TrimEnd('/')
if ($BaseUrl -notmatch '^https://[^/\s]+') {
    throw "BaseUrl 必须是 https 地址，例如 https://yourname.github.io/add-one-second-UWP（当前：$BaseUrl）"
}
$tileUrl = "$BaseUrl/tile.xml"
Write-Host "磁贴根地址 : $BaseUrl"
Write-Host "磁贴 XML   : $tileUrl"
Write-Host "仓库根目录 : $RepoRoot"
Write-Host ''

$manifest = Join-Path $RepoRoot 'addOneSecond\Package.appxmanifest'
$mainpage = Join-Path $RepoRoot 'addOneSecond\MainPage.xaml.cs'
$config = Join-Path $RepoRoot 'tile\config.json'

foreach ($f in @($manifest, $mainpage)) {
    if (-not (Test-Path $f)) { throw "找不到文件：$f（请在仓库根目录运行，或用 -RepoRoot 指定）" }
}

# ---- 1. Package.appxmanifest 的 UriTemplate ----
$m = Read-Utf8Text $manifest
if ($m.Text -notmatch 'UriTemplate="[^"]*"') { throw '清单里没找到 UriTemplate 属性，可能改错了文件' }
$m2 = [regex]::Replace($m.Text, 'UriTemplate="[^"]*"', "UriTemplate=`"$tileUrl`"")
Write-Utf8Text $manifest $m2 $m.HasBom
Write-Host "[ok] Package.appxmanifest -> $tileUrl"

# ---- 2. MainPage.xaml.cs 的 StartPeriodicUpdate 地址 ----
$c = Read-Utf8Text $mainpage
$pattern = '(\$?tileContent\s*=\s*new Uri\(")[^"]*(")'
if ($c.Text -notmatch $pattern) { throw 'MainPage.xaml.cs 里没找到 tileContent = new Uri("...")，可能改错了文件' }
$c2 = [regex]::Replace($c.Text, $pattern, "`${1}$tileUrl`${2}")
Write-Utf8Text $mainpage $c2 $c.HasBom
Write-Host "[ok] MainPage.xaml.cs      -> $tileUrl"

# ---- 3. tile/config.json 的 baseUrl（给生成器用）----
if (Test-Path $config) {
    $cfgFile = Read-Utf8Text $config
    if ($cfgFile.Text -match '"baseUrl"\s*:\s*"[^"]*"') {
        # 只替换值，保留原有缩进/键顺序
        $newCfg = [regex]::Replace($cfgFile.Text, '"baseUrl"\s*:\s*"[^"]*"', "`"baseUrl`": `"$BaseUrl`"")
        Write-Utf8Text $config $newCfg $cfgFile.HasBom
        Write-Host "[ok] tile/config.json      -> $BaseUrl"
    }
    else {
        Write-Warning 'tile/config.json 里没有 baseUrl 字段，跳过（生成器会按 TILE_BASE_URL / GITHUB_REPOSITORY 推算）'
    }
}
else {
    Write-Warning "没找到 tile/config.json，跳过（生成器会用演练推算或 TILE_BASE_URL）"
}

# ---- 4. 可选：立刻验一次 ----
if ($Verify) {
    Write-Host ''
    Write-Host "正在拉取 $tileUrl ..."
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object System.Net.Http.HttpClientHandler
    if ($Proxy) {
        $handler.Proxy = New-Object System.Net.WebProxy($Proxy, $true)
        $handler.UseProxy = $true
        Write-Host "（走代理 $Proxy）"
    }
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(25)
    try {
        $resp = $client.GetAsync($tileUrl).GetAwaiter().GetResult()
        $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Write-Host ("HTTP " + [int]$resp.StatusCode)
        if ($body -match '<tile') {
            Write-Host '[ok] 返回的是磁贴 XML' -ForegroundColor Green
            Write-Host ($body.Substring(0, [Math]::Min(300, $body.Length)))
        }
        else {
            Write-Host '[!] 返回内容不是磁贴 XML（部署还没生效？先看 /health.json）' -ForegroundColor Yellow
            Write-Host ($body.Substring(0, [Math]::Min(300, $body.Length)))
        }
    }
    catch {
        Write-Warning ("验活失败：" + $_.Exception.Message + "（首次部署前是正常的）")
    }
    finally {
        $client.Dispose()
    }
}

Write-Host ''
Write-Host '改完了。提交后记得在 Actions 里跑一次 Build UWP package，重新装包才会用上新地址。'
