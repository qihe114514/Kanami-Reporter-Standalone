[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$InstallerUrl = "https://github.com/qihe114514/Kanami-Reporter-Standalone/releases/latest/download/KanamiReporter-Setup-x64-$Version.exe"
)

$ErrorActionPreference = "Stop"
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
$manifest = [ordered]@{
    version = $Version
    installerUrl = $InstallerUrl
    sha256 = $hash
    releaseNotes = "Kanami Reporter $Version"
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
