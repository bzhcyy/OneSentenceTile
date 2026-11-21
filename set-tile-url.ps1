# 设置磁贴服务器地址（Worker + Pages 两个）
#
# 用法（在仓库根目录）：
#   powershell -ExecutionPolicy Bypass -File .\set-tile-url.ps1 -WorkerUrl https://onesentencetile.onesentencetile.workers.dev
#   powershell -ExecutionPolicy Bypass -File .\set-tile-url.ps1 -PagesUrl  https://bzhcyy.github.io/OneSentenceTile
#   powershell -ExecutionPolicy Bypass -File .\set-tile-url.ps1 -WorkerUrl https://a.workers.dev -PagesUrl https://b.github.io/x
#
# 只写其中一个也行，另一个保持原值。地址可以带 /tile.xml，也可以只写根，脚本会自动补。
#
# 它改三处：
#   1. OneSentenceTile/TileSource.cs 的 Worker / Pages 常量（App 运行时优先 Worker，连不上退 Pages）
#   2. OneSentenceTile/Package.appxmanifest 的 UriTemplate（App 没运行时用的默认地址，写 Pages）
#   3. tile/config.json 的 baseUrl（Pages 生成器用）
#
# 加 -Verify 会依次真的 GET 两个地址，看谁返回 <tile。

[CmdletBinding()]
param(
    [string]$WorkerUrl,
    [string]$PagesUrl,

    # 兼容旧用法：-BaseUrl 等同于 -WorkerUrl
    [string]$BaseUrl,

    [string]$RepoRoot,
    [string]$Proxy,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Read-Utf8Text([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    return [pscustomobject]@{ Text = [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8); Bom = $bom }
}
function Write-Utf8Text([string]$Path, [string]$Text, [bool]$Bom) {
    [System.IO.File]::WriteAllText($Path, $Text, (New-Object System.Text.UTF8Encoding($Bom)))
}
function Normalize-TileUrl([string]$url) {
    $u = $url.Trim().TrimEnd('/')
    if ($u -notmatch '^https://[^/\s]+') { throw "必须是 https 地址：$url" }
    if ($u -notmatch '\.xml$') { $u = "$u/tile.xml" }
    return $u
}

if (-not $RepoRoot) { $RepoRoot = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path } }
if (-not $WorkerUrl -and $BaseUrl) { $WorkerUrl = $BaseUrl }
if (-not $WorkerUrl -and -not $PagesUrl) {
    throw '至少给一个：-WorkerUrl 或 -PagesUrl（例：-WorkerUrl https://xxx.workers.dev）'
}

$tileSource = Join-Path $RepoRoot 'OneSentenceTile\TileSource.cs'
$manifest = Join-Path $RepoRoot 'OneSentenceTile\Package.appxmanifest'
$config = Join-Path $RepoRoot 'tile\config.json'
foreach ($f in @($tileSource, $manifest)) {
    if (-not (Test-Path $f)) { throw "找不到文件：$f（请在仓库根目录运行，或用 -RepoRoot 指定）" }
}

Write-Host "仓库根目录: $RepoRoot"

# ---------- 1. TileSource.cs ----------
$ts = Read-Utf8Text $tileSource
$text = $ts.Text
if ($WorkerUrl) {
    $w = Normalize-TileUrl $WorkerUrl
    $text = [regex]::Replace($text, 'public const string Worker = "[^"]*";', "public const string Worker = `"$w`";")
    Write-Host "[ok] Worker -> $w"
}
if ($PagesUrl) {
    $p = Normalize-TileUrl $PagesUrl
    $text = [regex]::Replace($text, 'public const string Pages = "[^"]*";', "public const string Pages = `"$p`";")
    Write-Host "[ok] Pages  -> $p"
}
Write-Utf8Text $tileSource $text $ts.Bom

# ---------- 2. manifest（默认地址用当前 Pages）----------
$pagesFinal = [regex]::Match($text, 'public const string Pages = "([^"]*)"').Groups[1].Value
$mf = Read-Utf8Text $manifest
if ($mf.Text -match 'UriTemplate="[^"]*"') {
    $m2 = [regex]::Replace($mf.Text, 'UriTemplate="[^"]*"', "UriTemplate=`"$pagesFinal`"")
    Write-Utf8Text $manifest $m2 $mf.Bom
    Write-Host "[ok] manifest UriTemplate -> $pagesFinal"
}

# ---------- 3. tile/config.json（Pages 生成器）----------
if (Test-Path $config) {
    $cf = Read-Utf8Text $config
    $pagesBase = $pagesFinal -replace '/tile\.xml$', ''
    if ($cf.Text -match '"baseUrl"\s*:\s*"[^"]*"') {
        $c2 = [regex]::Replace($cf.Text, '"baseUrl"\s*:\s*"[^"]*"', "`"baseUrl`": `"$pagesBase`"")
        Write-Utf8Text $config $c2 $cf.Bom
        Write-Host "[ok] tile/config.json baseUrl -> $pagesBase"
    }
}

# ---------- 4. 可选验活 ----------
if ($Verify) {
    Write-Host ''
    Add-Type -AssemblyName System.Net.Http
    $pairs = @()
    $wFinal = [regex]::Match($text, 'public const string Worker = "([^"]*)"').Groups[1].Value
    if ($wFinal) { $pairs += @{ Name = 'Worker'; Url = $wFinal } }
    if ($pagesFinal) { $pairs += @{ Name = 'Pages'; Url = $pagesFinal } }

    foreach ($item in $pairs) {
        $handler = New-Object System.Net.Http.HttpClientHandler
        if ($Proxy) {
            $handler.Proxy = New-Object System.Net.WebProxy($Proxy, $true)
            $handler.UseProxy = $true
        }
        $client = New-Object System.Net.Http.HttpClient($handler)
        $client.Timeout = [TimeSpan]::FromSeconds(12)
        try {
            $resp = $client.GetAsync($item.Url).GetAwaiter().GetResult()
            $body = $resp.Content.ReadAsStringAsync().GetAwaiter().GetResult()
            $ok = $body -match '<tile'
            $mark = if ($ok) { '可用' } else { '返回内容不是磁贴 XML' }
            Write-Host ("[{0}] HTTP {1}  {2}  {3}" -f $item.Name, [int]$resp.StatusCode, $mark, $item.Url)
        }
        catch {
            Write-Host ("[{0}] 连不上：{1}  {2}" -f $item.Name, $_.Exception.Message.Split("`n")[0], $item.Url) -ForegroundColor Yellow
        }
        finally { $client.Dispose() }
    }
    Write-Host ''
    Write-Host '提示：Worker 不通属正常（workers.dev 在国内常被 DNS 污染），App 会自动退回 Pages。' -ForegroundColor Cyan
}

Write-Host ''
Write-Host '改完提交，再跑一次 Build UWP package 装新包（地址是编译进包里的，必须重装）。'
