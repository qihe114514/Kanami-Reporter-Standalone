[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Repo = 'qihe114514/Kanami-Reporter-Standalone',
    [string]$ReleaseDir = 'artifacts\release',
    [string]$TagPrefix = 'v',
    [string]$Proxy
)

# 手动发版脚本：CI 的 Publish GitHub Release 步骤因为 workflow 缺少 contents: write 权限一直失败
# （v1.0.0 / 1.0.4 / 1.0.5 都是），所以 release 与 assets 历来靠 API 手动创建。
#
#   powershell -ExecutionPolicy Bypass -File scripts\Publish-Release.ps1 -Version 1.0.8
#
# 前提：先跑 Build-Release.ps1 生成 artifacts\release\ 下的产物，并写好
#       artifacts\release-notes-<版本>.md（正文与 release 标题都从这个文件读，
#       所以本脚本保持纯 ASCII，避免 PowerShell 5.1 按 ANSI 读中文源码时解析失败）。
# 可重复执行：release 已存在就复用，同名 asset 已上传就跳过。

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$tag = "$TagPrefix$Version"
$notesPath = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\release-notes-$Version.md"
if (-not (Test-Path -LiteralPath $notesPath)) {
    throw "release notes not found: $notesPath"
}

foreach ($required in @(
        (Join-Path $ReleaseDir "KanamiReporter-Setup-x64-$Version.exe"),
        (Join-Path $ReleaseDir "KanamiReporter-$Version-x64.zip"),
        (Join-Path $ReleaseDir 'SHA256SUMS.txt'),
        (Join-Path $ReleaseDir 'update.json'))) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "missing release artifact: $required (run Build-Release.ps1 first)"
    }
}

# Token 从 Git 凭据管理器取，不落盘、不打印。
# 注意：凭据管理器偶尔会往 stderr 写日志，而 PS 5.1 在 ErrorActionPreference=Stop 下会把
# 原生命令的 stderr 当成终止性错误、连 stdout 一起丢掉，所以这里临时放宽并合并两个流。
$credential = @()
try {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $credential = @("protocol=https`nhost=github.com`n`n" | git credential fill 2>&1)
} finally {
    $ErrorActionPreference = $previousPreference
}

$token = ($credential | Where-Object { $_ -is [string] -and $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
if (-not $token) {
    throw 'token not found in git credential manager'
}

$headers = @{ Authorization = "token $token"; 'User-Agent' = 'KanamiRelease' }
$notes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $notesPath).Path, [Text.Encoding]::UTF8)
$name = (($notes -split "`r?`n")[0]) -replace '^#+\s*', ''

# Optional explicit proxy, e.g. -Proxy http://127.0.0.1:7897 (the system proxy).
# Without it, .NET uses the system/WinINET proxy settings by default.
$proxyArgs = @{}
if ($Proxy) {
    $proxyArgs['Proxy'] = $Proxy
    "using proxy: $Proxy"
}

$release = $null
try {
    $release = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$Repo/releases/tags/$tag" -Headers $headers -TimeoutSec 60 @proxyArgs
    "release $tag already exists (id=$($release.id)), reusing it"
} catch {
    $release = $null
}

if (-not $release) {
    $payload = [ordered]@{
        tag_name         = $tag
        target_commitish = 'main'
        name             = $name
        body             = $notes
        draft            = $false
        prerelease       = $false
    } | ConvertTo-Json -Depth 4
    $bodyBytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $release = Invoke-RestMethod -Method Post -Uri "https://api.github.com/repos/$Repo/releases" `
        -Headers $headers -ContentType 'application/json; charset=utf-8' -Body $bodyBytes -TimeoutSec 120 @proxyArgs
    "created release: id=$($release.id) tag=$($release.tag_name)"
}

$uploaded = @{}
foreach ($asset in $release.assets) { $uploaded[$asset.name] = $asset.size }

foreach ($path in @(
        (Join-Path $ReleaseDir "KanamiReporter-Setup-x64-$Version.exe"),
        (Join-Path $ReleaseDir "KanamiReporter-$Version-x64.zip"),
        (Join-Path $ReleaseDir 'SHA256SUMS.txt'),
        (Join-Path $ReleaseDir 'update.json'))) {
    $item = Get-Item -LiteralPath $path
    if ($uploaded.ContainsKey($item.Name)) {
        "skip (already uploaded): $($item.Name)"
        continue
    }

    "uploading $($item.Name) ($([math]::Round($item.Length / 1MB, 1)) MB)"
    $result = Invoke-RestMethod -Method Post `
        -Uri "https://uploads.github.com/repos/$Repo/releases/$($release.id)/assets?name=$($item.Name)" `
        -Headers $headers -ContentType 'application/octet-stream' -InFile $item.FullName -TimeoutSec 1800 @proxyArgs
    "  uploaded: $($result.name) state=$($result.state) size=$($result.size)"
}

$final = Invoke-RestMethod -Method Get -Uri "https://api.github.com/repos/$Repo/releases/tags/$tag" -Headers $headers -TimeoutSec 60 @proxyArgs
"release url: $($final.html_url)"
foreach ($a in $final.assets) { "  - $($a.name) $($a.size) bytes" }
