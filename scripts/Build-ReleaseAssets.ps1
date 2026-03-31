param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDir,

    [Parameter(Mandatory = $true)]
    [string]$AppVersion,

    [Parameter(Mandatory = $true)]
    [string]$ConfigsVersion,

    [Parameter(Mandatory = $true)]
    [string]$AudioPackVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ReleaseDir -PathType Container)) {
    throw "ReleaseDir not found: $ReleaseDir"
}

$requiredPatterns = @(
    'app-win-x86.zip',
    'configs.zip',
    'audio-manifest.json',
    'audio-files-*.zip'
)

foreach ($pattern in $requiredPatterns) {
    $found = Get-ChildItem -LiteralPath $ReleaseDir -Filter $pattern -File -ErrorAction SilentlyContinue
    if (-not $found) {
        throw "Required asset missing for pattern: $pattern"
    }
}

$releaseStatePath = Join-Path $ReleaseDir 'release-state.json'
$releaseState = [ordered]@{
    appVersion = $AppVersion
    configsVersion = $ConfigsVersion
    audioPackVersion = $AudioPackVersion
}
$releaseState | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $releaseStatePath -Encoding UTF8

$checksumPath = Join-Path $ReleaseDir 'checksums.txt'
$assets = Get-ChildItem -LiteralPath $ReleaseDir -File |
    Where-Object { $_.Name -ne 'checksums.txt' } |
    Sort-Object Name

$lines = foreach ($asset in $assets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
}

Set-Content -LiteralPath $checksumPath -Value $lines -Encoding UTF8

Write-Host "Release assets prepared successfully." -ForegroundColor Green
Write-Host "ReleaseDir: $ReleaseDir"
Write-Host "Generated: release-state.json, checksums.txt"
