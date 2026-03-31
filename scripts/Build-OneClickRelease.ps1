param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDir,

    [Parameter(Mandatory = $true)]
    [string]$AppVersion,

    [Parameter(Mandatory = $true)]
    [string]$ConfigsVersion,

    [Parameter(Mandatory = $true)]
    [string]$AudioPackVersion,

    [string]$Configuration = 'Release',

    [string]$RuntimeIdentifier = 'win-x86',

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\JRETS.Go.App\JRETS.Go.App.csproj'
$updaterProject = Join-Path $repoRoot 'src\JRETS.Go.Updater\JRETS.Go.Updater.csproj'
$configsDir = Join-Path $repoRoot 'src\JRETS.Go.App\configs'
$audioDir = Join-Path $repoRoot 'src\JRETS.Go.App\audio'
$assetFinalizeScript = Join-Path $PSScriptRoot 'Build-ReleaseAssets.ps1'

if (-not (Test-Path -LiteralPath $appProject -PathType Leaf)) {
    throw "App project not found: $appProject"
}

if (-not (Test-Path -LiteralPath $updaterProject -PathType Leaf)) {
    throw "Updater project not found: $updaterProject"
}

if (-not (Test-Path -LiteralPath $configsDir -PathType Container)) {
    throw "Configs directory not found: $configsDir"
}

if (-not (Test-Path -LiteralPath $audioDir -PathType Container)) {
    throw "Audio directory not found: $audioDir"
}

New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("JRETS.Go.ReleaseBuild\" + [Guid]::NewGuid().ToString('N'))
$appPublishDir = Join-Path $stagingRoot 'app-publish'

try {
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $appPublishDir | Out-Null

    if (-not $SkipBuild) {
        Write-Host "[1/6] Building updater project..." -ForegroundColor Cyan
        dotnet build $updaterProject -c $Configuration | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "Updater build failed."
        }

        Write-Host "[2/6] Publishing app project..." -ForegroundColor Cyan
        dotnet publish $appProject -c $Configuration -r $RuntimeIdentifier --self-contained true -o $appPublishDir /p:PublishSingleFile=false /p:PublishReadyToRun=true | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "App publish failed."
        }
    }

    $appZipPath = Join-Path $ReleaseDir 'app-win-x86.zip'
    if (Test-Path -LiteralPath $appZipPath) {
        Remove-Item -LiteralPath $appZipPath -Force
    }

    Write-Host "[3/6] Packing app-win-x86.zip..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $appPublishDir '*') -DestinationPath $appZipPath -CompressionLevel Optimal

    $configsZipPath = Join-Path $ReleaseDir 'configs.zip'
    if (Test-Path -LiteralPath $configsZipPath) {
        Remove-Item -LiteralPath $configsZipPath -Force
    }

    Write-Host "[4/6] Packing configs.zip..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $configsDir '*') -DestinationPath $configsZipPath -CompressionLevel Optimal

    $audioZipName = 'audio-files-base.zip'
    $audioZipPath = Join-Path $ReleaseDir $audioZipName
    if (Test-Path -LiteralPath $audioZipPath) {
        Remove-Item -LiteralPath $audioZipPath -Force
    }

    Write-Host "[5/6] Packing $audioZipName and generating audio-manifest.json..." -ForegroundColor Cyan
    Compress-Archive -Path (Join-Path $audioDir '*') -DestinationPath $audioZipPath -CompressionLevel Optimal

    $audioFiles = Get-ChildItem -LiteralPath $audioDir -File -Recurse | Sort-Object FullName
        $audioBaseUri = [System.Uri]((Resolve-Path -LiteralPath $audioDir).Path.TrimEnd('\\') + '\\')
    $manifestFiles = foreach ($file in $audioFiles) {
            $fileUri = [System.Uri](Resolve-Path -LiteralPath $file.FullName).Path
            $relativePath = $audioBaseUri.MakeRelativeUri([System.Uri]$fileUri).ToString().Replace('%20', ' ')
        $sha = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

        [ordered]@{
            relativePath = $relativePath
            sha256 = $sha
            sizeBytes = [int64]$file.Length
            packageName = $audioZipName
        }
    }

    $manifest = [ordered]@{
        version = $AudioPackVersion
        files = $manifestFiles
        deleteFiles = @()
        minimumClientVersion = $AppVersion
        publishedAt = (Get-Date).ToUniversalTime().ToString('o')
    }

    $audioManifestPath = Join-Path $ReleaseDir 'audio-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $audioManifestPath -Encoding UTF8

    Write-Host "[6/6] Generating release-state.json and checksums.txt..." -ForegroundColor Cyan
    & $assetFinalizeScript -ReleaseDir $ReleaseDir -AppVersion $AppVersion -ConfigsVersion $ConfigsVersion -AudioPackVersion $AudioPackVersion

    Write-Host "One-click release build completed." -ForegroundColor Green
    Write-Host "Release output: $ReleaseDir"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
