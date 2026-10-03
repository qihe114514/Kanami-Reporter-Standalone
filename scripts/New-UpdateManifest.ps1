[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [string]$InstallerUrl = "https://github.com/qihe114514/Kanami-Reporter-Standalone/releases/latest/download/KanamiReporter-Setup-x64-$Version.exe",
    [string]$ReleaseNotesPath
)

$ErrorActionPreference = "Stop"
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash

# The in-app update prompt shows releaseNotes verbatim and has limited room, so only the
# first paragraph of artifacts\release-notes-<Version>.md is used (markdown marks stripped).
# NOTE: keep this script ASCII-only (no BOM -> Windows PowerShell 5.1 reads it as ANSI).
$releaseNotes = "Kanami Reporter $Version"
if (-not $ReleaseNotesPath) {
    $ReleaseNotesPath = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\release-notes-$Version.md"
}

if (Test-Path -LiteralPath $ReleaseNotesPath) {
    $paragraph = New-Object System.Collections.Generic.List[string]
    $started = $false
    foreach ($line in (Get-Content -LiteralPath $ReleaseNotesPath -Encoding UTF8)) {
        $trimmed = $line.Trim()
        if (-not $started) {
            if ($trimmed -eq "" -or $trimmed.StartsWith("#")) { continue }
            $started = $true
        }
        elseif ($trimmed -eq "") {
            break
        }

        $paragraph.Add($trimmed)
    }

    if ($paragraph.Count -gt 0) {
        $releaseNotes = (($paragraph -join "") -replace '\*\*', '' -replace '`', '').Trim()
    }
}

$manifest = [ordered]@{
    version = $Version
    installerUrl = $InstallerUrl
    sha256 = $hash
    releaseNotes = $releaseNotes
}

$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
