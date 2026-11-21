# 一键把本地项目同步到 GitHub（不需要装 Git）
#
# 用法（在本脚本所在目录）：
#   powershell -ExecutionPolicy Bypass -File .\push-to-github.ps1 -Token ghp_xxxxxxxx
#
# 可选参数：
#   -Repo    bzhcyy/add-one-second-UWP      仓库（默认这个）
#   -Branch  master                         分支
#   -Path    <脚本所在目录>                  要上传的本地目录
#   -Message "更新磁贴"                      提交说明
#   -Proxy   http://127.0.0.1:7897           走代理时填
#   -DryRun                                  只列出将新增/修改/删除的文件，不真的提交
#
# 它做的事：
#   1. 读远端分支最新的 tree，算出每个文件的 SHA1（git blob 算法）
#   2. 本地比远端多/不一样的 → 上传；远端有而本地没有的 → 删除
#   3. 用 Git Data API 打成一个 commit 再更新分支（不会有 100 个碎提交）
#
# 关于 Token：
#   GitHub → Settings → Developer settings → Personal access tokens →
#   Fine-grained tokens → 新建，Repository access 选这个仓库，
#   Permissions → Repository permissions → Contents 选 Read and write。
#   （Classic token 勾 repo 也可以。）脚本不会保存 token。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Token,

    [string]$Repo = 'bzhcyy/add-one-second-UWP',
    [string]$Branch = 'master',
    [string]$Path = $(if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }),
    [string]$Message = '更新磁贴内容（每日一言）',
    [string]$Proxy,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---------- Token 预检查（最常见的两种错） ----------
$Token = $Token.Trim().Trim('"').Trim("'")
if ($Token -match '\s') { throw 'Token 里含有空格/换行，重新复制一次（整串不能断开）' }
if ($Token -like 'ghp_github_pat_*') {
    $fixed = $Token.Substring(4)
    Write-Warning 'Token 前面多了 ghp_ 前缀（细粒度 Token 本身就以 github_pat_ 开头），已自动去掉。'
    $Token = $fixed
}
elseif ($Token -like 'github_pat_*') {
    Write-Host 'Token 类型: 细粒度（github_pat_…）'
}
elseif ($Token -like 'ghp_*') {
    Write-Host 'Token 类型: classic（ghp_…，需要勾 repo 权限）'
}
else {
    throw "Token 格式不认识（应以 github_pat_ 或 ghp_ 开头，当前是 '$($Token.Substring(0, [Math]::Min(8, $Token.Length)))…'）。请在 GitHub → Settings → Developer settings → Personal access tokens 里复制完整令牌。"
}

$ApiBase = "https://api.github.com"
$SelfName = [IO.Path]::GetFileName($MyInvocation.MyCommand.Path)
$Headers = @{
    'Authorization'        = "Bearer $Token"
    'Accept'               = 'application/vnd.github+json'
    'User-Agent'           = 'push-to-github-script'
    'X-GitHub-Api-Version' = '2022-11-28'
}
$ProxyArgs = @{}
if ($Proxy) { $ProxyArgs['Proxy'] = $Proxy }

# 先用 /user 验一次 Token，报错信息更直白
try {
    $me = Invoke-RestMethod -Uri "$ApiBase/user" -Headers $Headers -TimeoutSec 30 -ErrorAction Stop @ProxyArgs
    Write-Host "Token 有效，登录身份: $($me.login)"
}
catch {
    $status = $null
    try { $status = [int]$_.Exception.Response.StatusCode } catch { }
    $detail = $_.Exception.Message
    try {
        $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
        $detail += "`n" + $sr.ReadToEnd()
    }
    catch { }

    if ($status -eq 401) {
        throw @"
Token 无效（401 Bad credentials）：
  1. 复制时多带了前缀（细粒度 token 以 github_pat_ 开头，不要再加 ghp_）或漏了字符；
  2. Token 被删除或 Regenerate 过（旧的立即失效）；
  3. 复制时带上了空格或换行。
原始返回：$detail
"@
    }
    elseif ($status -eq 403) {
        throw "Token 权限不足（403）。到 Token 设置里把 Repository permissions → Contents 改成 Read and write，必要时删掉旧 token 重新生成。`n原始返回：$detail"
    }
    else {
        throw "连不上 GitHub API（$detail）。`n如果是网络问题，加 -Proxy http://127.0.0.1:7897 再试。"
    }
}

function Invoke-GitHub {
    param([string]$Method, [string]$Uri, $Body)
    $req = @{
        Method      = $Method
        Uri         = $Uri
        Headers     = $Headers
        TimeoutSec  = 60
        ErrorAction = 'Stop'
    } + $ProxyArgs
    if ($null -ne $Body) {
        $req['Body'] = ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 10 -Compress)))
        $req['ContentType'] = 'application/json; charset=utf-8'
    }
    try {
        return Invoke-RestMethod @req
    }
    catch {
        $status = $null
        try { $status = [int]$_.Exception.Response.StatusCode } catch { }
        $msg = $_.Exception.Message
        try {
            $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
            $msg += "`n" + $sr.ReadToEnd()
        }
        catch { }
        if ($status -eq 403) {
            throw @"
GitHub 拒绝了这次请求（403）。最常见原因：
  1. 改动了 .github/workflows/ 下的文件，但 Token 没有 Workflows 权限
     -> Token 设置里加 Repository permissions -> Workflows: Read and write；
  2. Token 的 Repository access 没勾上 bzhcyy/add-one-second-UWP；
  3. Contents 不是 Read and write。
接口：$Method $Uri
原始返回：$msg
"@
        }
        throw "GitHub API 失败（$Method $Uri）：$msg"
    }
}

# ---------- 计算 git blob SHA1 ----------
function Get-GitBlobSha([byte[]]$Bytes) {
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($Bytes.Length)`0")
    $sha1 = [Security.Cryptography.SHA1]::Create()
    $all = New-Object byte[] ($header.Length + $Bytes.Length)
    [Array]::Copy($header, 0, $all, 0, $header.Length)
    [Array]::Copy($Bytes, 0, $all, $header.Length, $Bytes.Length)
    $hash = $sha1.ComputeHash($all)
    return (-join ($hash | ForEach-Object { $_.ToString('x2') }))
}

Write-Host "仓库    : $Repo (分支 $Branch)" -ForegroundColor Cyan
Write-Host "本地目录: $Path" -ForegroundColor Cyan

if (-not (Test-Path $Path)) { throw "目录不存在：$Path" }

# ---------- 1. 远端当前 tree ----------
$branchInfo = Invoke-GitHub 'GET' "$ApiBase/repos/$Repo/branches/$Branch"
$baseCommitSha = $branchInfo.commit.sha
$baseTreeSha = $branchInfo.commit.commit.tree.sha
Write-Host "远端 HEAD: $baseCommitSha"

$remoteTree = @{}
$tree = Invoke-GitHub 'GET' "$ApiBase/repos/$Repo/git/trees/$baseTreeSha`?recursive=1"
foreach ($item in $tree.tree) {
    if ($item.type -eq 'blob') { $remoteTree[$item.path] = $item.sha }
}
Write-Host "远端文件数: $($remoteTree.Count)"

# ---------- 2. 本地文件 ----------
$skipDirs = @('.git', '_site', 'build', 'node_modules', '.vs', 'AppPackages')
$localFiles = @{}
Get-ChildItem -Path $Path -Recurse -File -Force | ForEach-Object {
    $rel = $_.FullName.Substring($Path.Length).TrimStart('\', '/') -replace '\\', '/'
    $top = ($rel -split '/')[0]
    if ($skipDirs -contains $top) { return }
    if ($_.Name -like '*.pfx') { return }                            # 私钥绝不外传
    $bytes = [IO.File]::ReadAllBytes($_.FullName)
    $localFiles[$rel] = [pscustomobject]@{
        Bytes = $bytes
        Sha   = Get-GitBlobSha $bytes
    }
}
Write-Host "本地文件数: $($localFiles.Count)"

$toAdd = @(); $toUpdate = @(); $toDelete = @()
foreach ($rel in $localFiles.Keys) {
    if (-not $remoteTree.ContainsKey($rel)) { $toAdd += $rel }
    elseif ($remoteTree[$rel] -ne $localFiles[$rel].Sha) { $toUpdate += $rel }
}
foreach ($rel in $remoteTree.Keys) {
    if (-not $localFiles.ContainsKey($rel)) { $toDelete += $rel }
}
$toAdd = @($toAdd | Sort-Object); $toUpdate = @($toUpdate | Sort-Object); $toDelete = @($toDelete | Sort-Object)

Write-Host ""
Write-Host "新增 $($toAdd.Count) 个：" -ForegroundColor Green
$toAdd | ForEach-Object { Write-Host "  + $_" }
Write-Host "修改 $($toUpdate.Count) 个：" -ForegroundColor Yellow
$toUpdate | ForEach-Object { Write-Host "  ~ $_" }
Write-Host "删除 $($toDelete.Count) 个：" -ForegroundColor Red
$toDelete | ForEach-Object { Write-Host "  - $_" }

$workflowChanges = @(@($toAdd + $toUpdate + $toDelete) | Where-Object { $_ -like '.github/workflows/*' })
if ($workflowChanges.Count -gt 0) {
    Write-Host ''
    Write-Warning ("这次会改动 workflow 文件：" + ($workflowChanges -join ', '))
    Write-Warning '细粒度 Token 必须额外勾上 Repository permissions -> Workflows: Read and write，'
    Write-Warning '否则创建 tree 时会被 GitHub 拒绝（403 已禁止）。'
}

if ($toAdd.Count + $toUpdate.Count + $toDelete.Count -eq 0) {
    Write-Host "`n远端已经是最新的，无需提交。" -ForegroundColor Green
    return
}
if ($DryRun) { Write-Host "`n[DryRun] 没有提交任何东西。" -ForegroundColor Cyan; return }

# ---------- 3. 上传变更的 blob ----------
$entries = @()
$i = 0
foreach ($rel in @($toAdd + $toUpdate)) {
    $i++
    Write-Progress -Activity '上传文件' -Status "$i / $($toAdd.Count + $toUpdate.Count)  $rel" `
        -PercentComplete ([int](100 * $i / [Math]::Max(1, $toAdd.Count + $toUpdate.Count)))
    $blob = Invoke-GitHub 'POST' "$ApiBase/repos/$Repo/git/blobs" @{
        content  = [Convert]::ToBase64String($localFiles[$rel].Bytes)
        encoding = 'base64'
    }
    $entries += @{ path = $rel; mode = '100644'; type = 'blob'; sha = $blob.sha }
}
Write-Progress -Activity '上传文件' -Completed

# ---------- 4. 删除远端多余文件（tree 条目 sha=null）----------
foreach ($rel in $toDelete) {
    $entries += @{ path = $rel; mode = '100644'; type = 'blob'; sha = $null }
}

if ($entries.Count -eq 0) { Write-Host "没有需要提交的改动。"; return }

$newTree = Invoke-GitHub 'POST' "$ApiBase/repos/$Repo/git/trees" @{
    base_tree = $baseTreeSha
    tree      = $entries
}

$newCommit = Invoke-GitHub 'POST' "$ApiBase/repos/$Repo/git/commits" @{
    message = $Message
    tree    = $newTree.sha
    parents = @($baseCommitSha)
}

$null = Invoke-GitHub 'PATCH' "$ApiBase/repos/$Repo/git/refs/heads/$Branch" @{
    sha   = $newCommit.sha
    force = $false
}

Write-Host ""
Write-Host "✅ 已提交：$($newCommit.sha)" -ForegroundColor Green
Write-Host "   新增 $($toAdd.Count) / 修改 $($toUpdate.Count) / 删除 $($toDelete.Count)"
Write-Host "   https://github.com/$Repo/commit/$($newCommit.sha)"
Write-Host ""
Write-Host "接着去 Actions 跑一次 Publish live tile to GitHub Pages 即可生效。" -ForegroundColor Cyan
