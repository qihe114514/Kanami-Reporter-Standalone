[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0",
    [switch]$SkipTests,
    [switch]$SkipInstaller,
    [string]$DotnetPath
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnet = if ($DotnetPath) { (Resolve-Path -LiteralPath $DotnetPath).Path } elseif ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT "dotnet.exe" } else { "dotnet" }
$publishDir = Join-Path $root "artifacts\publish\win-x64"
$releaseDir = Join-Path $root "artifacts\release"

Push-Location $root
try {
    & $dotnet restore "KanamiReporter.sln"
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore 失败。" }
    if (-not $SkipTests) {
        & $dotnet test "KanamiReporter.sln" -c $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet test 失败。" }
    }

    $publishFull = [System.IO.Path]::GetFullPath($publishDir)
    $rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    if (-not $publishFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理工作区外的发布目录：$publishFull"
    }

    if (Test-Path -LiteralPath $publishFull) {
        Remove-Item -LiteralPath $publishFull -Recurse -Force
    }

    & $dotnet publish "src\KanamiReporter.App\KanamiReporter.App.csproj" `
        -c $Configuration `
        -r win-x64 `
        --self-contained true `
        -p:Version=$Version `
        -p:DebugType=embedded `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败。" }

    New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
    $portableZip = Join-Path $releaseDir "KanamiReporter-$Version-x64.zip"
    if (Test-Path -LiteralPath $portableZip) {
        Remove-Item -LiteralPath $portableZip -Force
    }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $portableZip -CompressionLevel Optimal

    if (-not $SkipInstaller) {
        $compiler = $env:INNO_SETUP_COMPILER
        if (-not $compiler) {
            $candidates = @(
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
            )
            $compiler = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        }

        if (-not $compiler) {
            Write-Warning "未找到 Inno Setup 6 编译器，已跳过安装器。设置 INNO_SETUP_COMPILER 或安装 Inno Setup 6 后重试。"
            return
        }

        New-Item -ItemType Directory -Force -Path $releaseDir | Out-Null
        & $compiler `
            "/DMyAppVersion=$Version" `
            "/DSourceDir=$publishDir" `
            "/DOutputDir=$releaseDir" `
            "installer\KanamiReporter.iss"
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败。" }

        $installer = Join-Path $releaseDir "KanamiReporter-Setup-x64-$Version.exe"
        & (Join-Path $PSScriptRoot "New-UpdateManifest.ps1") `
            -Version $Version `
            -InstallerPath $installer `
            -OutputPath (Join-Path $releaseDir "update.json")

        $hashFiles = @($portableZip, $installer, (Join-Path $releaseDir "update.json"))
        $hashLines = foreach ($file in $hashFiles) {
            $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            "$hash  $(Split-Path -Leaf $file)"
        }
        $hashLines | Set-Content -LiteralPath (Join-Path $releaseDir "SHA256SUMS.txt") -Encoding utf8
    }
}
finally {
    Pop-Location
}


