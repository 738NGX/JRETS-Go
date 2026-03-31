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

    [ValidateSet('full', 'delta')]
    [string]$AudioPackageMode = 'full',

    [string]$PreviousAudioManifestPath,

    [int]$AudioPackageMaxSizeMb = 80,

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

if ($AudioPackageMaxSizeMb -le 0) {
    throw "AudioPackageMaxSizeMb must be greater than 0."
}

if ($AudioPackageMode -eq 'delta' -and [string]::IsNullOrWhiteSpace($PreviousAudioManifestPath)) {
    throw "PreviousAudioManifestPath is required when AudioPackageMode=delta."
}

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
$audioPackageStageRoot = Join-Path $stagingRoot 'audio-package-stage'

function Get-AudioRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory,
        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $baseUri = [System.Uri]((Resolve-Path -LiteralPath $BaseDirectory).Path.TrimEnd('\\') + '\\')
    $fileUri = [System.Uri](Resolve-Path -LiteralPath $FilePath).Path
    return $baseUri.MakeRelativeUri([System.Uri]$fileUri).ToString().Replace('%20', ' ')
}

function New-AudioPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageName,
        [AllowEmptyCollection()]
        [object[]]$Files = @(),
        [Parameter(Mandatory = $true)]
        [string]$AudioRoot,
        [Parameter(Mandatory = $true)]
        [string]$StageRoot,
        [Parameter(Mandatory = $true)]
        [string]$OutputDirectory
    )

    $packageStage = Join-Path $StageRoot ([System.IO.Path]::GetFileNameWithoutExtension($PackageName))
    if (Test-Path -LiteralPath $packageStage) {
        Remove-Item -LiteralPath $packageStage -Recurse -Force
    }

    New-Item -ItemType Directory -Force -Path $packageStage | Out-Null

    foreach ($file in $Files) {
        $relativePath = Get-AudioRelativePath -BaseDirectory $AudioRoot -FilePath $file.FullName
        $relativePath = $relativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        $destination = Join-Path $packageStage $relativePath
        $destinationDirectory = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $destinationDirectory)) {
            New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }

    $outputPath = Join-Path $OutputDirectory $PackageName
    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Force
    }

    if ((Get-ChildItem -LiteralPath $packageStage -File -Recurse | Measure-Object).Count -eq 0) {
        $placeholder = Join-Path $packageStage '.placeholder'
        Set-Content -LiteralPath $placeholder -Value 'placeholder' -Encoding UTF8
    }

    Compress-Archive -Path (Join-Path $packageStage '*') -DestinationPath $outputPath -CompressionLevel Optimal
}

try {
    New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
    New-Item -ItemType Directory -Force -Path $appPublishDir | Out-Null
    New-Item -ItemType Directory -Force -Path $audioPackageStageRoot | Out-Null

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

    Write-Host "[5/6] Packing audio packages and generating audio-manifest.json (mode=$AudioPackageMode)..." -ForegroundColor Cyan

    Get-ChildItem -LiteralPath $ReleaseDir -Filter 'audio-files-*.zip' -File -ErrorAction SilentlyContinue |
        Remove-Item -Force

    $audioFiles = Get-ChildItem -LiteralPath $audioDir -File -Recurse | Sort-Object FullName

    $currentAudioMap = @{}
    foreach ($file in $audioFiles) {
        $relativePath = Get-AudioRelativePath -BaseDirectory $audioDir -FilePath $file.FullName
        $sha = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $currentAudioMap[$relativePath] = [ordered]@{
            file = $file
            sha256 = $sha
        }
    }

    $previousAudioMap = @{}
    $deleteFiles = @()

    if ($AudioPackageMode -eq 'delta') {
        if (-not (Test-Path -LiteralPath $PreviousAudioManifestPath -PathType Leaf)) {
            throw "PreviousAudioManifestPath not found: $PreviousAudioManifestPath"
        }

        $previousManifestRaw = Get-Content -LiteralPath $PreviousAudioManifestPath -Raw
        $previousManifest = $previousManifestRaw | ConvertFrom-Json
        if ($null -eq $previousManifest -or $null -eq $previousManifest.files) {
            throw "Previous audio manifest is malformed: $PreviousAudioManifestPath"
        }

        foreach ($entry in $previousManifest.files) {
            if ($null -ne $entry.relativePath -and $null -ne $entry.sha256) {
                $previousAudioMap[[string]$entry.relativePath] = [string]$entry.sha256
            }
        }

        foreach ($previousRelativePath in $previousAudioMap.Keys) {
            if (-not $currentAudioMap.ContainsKey($previousRelativePath)) {
                $deleteFiles += $previousRelativePath
            }
        }
    }

    $changedEntries = @()
    foreach ($relativePath in $currentAudioMap.Keys) {
        $currentEntry = $currentAudioMap[$relativePath]
        if ($AudioPackageMode -eq 'full') {
            $changedEntries += [ordered]@{
                relativePath = $relativePath
                file = $currentEntry.file
                sha256 = $currentEntry.sha256
                sizeBytes = [int64]$currentEntry.file.Length
            }
            continue
        }

        if (-not $previousAudioMap.ContainsKey($relativePath) -or $previousAudioMap[$relativePath].ToLowerInvariant() -ne $currentEntry.sha256) {
            $changedEntries += [ordered]@{
                relativePath = $relativePath
                file = $currentEntry.file
                sha256 = $currentEntry.sha256
                sizeBytes = [int64]$currentEntry.file.Length
            }
        }
    }

    $maxPackageBytes = [int64]$AudioPackageMaxSizeMb * 1MB
    $packageGroups = @()
    $currentGroup = @()
    $currentGroupBytes = 0L

    foreach ($entry in ($changedEntries | Sort-Object relativePath)) {
        $entryBytes = [int64]$entry.sizeBytes
        $wouldOverflow = $currentGroup.Count -gt 0 -and (($currentGroupBytes + $entryBytes) -gt $maxPackageBytes)

        if ($wouldOverflow) {
            $packageGroups += ,$currentGroup
            $currentGroup = @()
            $currentGroupBytes = 0L
        }

        $currentGroup += $entry
        $currentGroupBytes += $entryBytes
    }

    if ($currentGroup.Count -gt 0) {
        $packageGroups += ,$currentGroup
    }

    if ($packageGroups.Count -eq 0) {
        $packageGroups = ,@()
    }

    $manifestFiles = @()
    for ($i = 0; $i -lt $packageGroups.Count; $i++) {
        $packageIndex = $i + 1
        $packageName = if ($packageGroups.Count -eq 1) {
            'audio-files-001.zip'
        }
        else {
            ('audio-files-{0:d3}.zip' -f $packageIndex)
        }

        $group = $packageGroups[$i]
        $groupFiles = @($group | ForEach-Object { $_.file })

        New-AudioPackage -PackageName $packageName -Files $groupFiles -AudioRoot $audioDir -StageRoot $audioPackageStageRoot -OutputDirectory $ReleaseDir

        foreach ($entry in $group) {
            $manifestFiles += [ordered]@{
                relativePath = $entry.relativePath
                sha256 = $entry.sha256
                sizeBytes = $entry.sizeBytes
                packageName = $packageName
            }
        }
    }

    $manifest = [ordered]@{
        version = $AudioPackVersion
        files = $manifestFiles
        deleteFiles = @($deleteFiles | Sort-Object -Unique)
        minimumClientVersion = $AppVersion
        publishedAt = (Get-Date).ToUniversalTime().ToString('o')
    }

    $audioManifestPath = Join-Path $ReleaseDir 'audio-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $audioManifestPath -Encoding UTF8

    if ($AudioPackageMode -eq 'delta') {
        Write-Host ("Audio delta summary: changed={0}, deleted={1}, packages={2}" -f $changedEntries.Count, $deleteFiles.Count, $packageGroups.Count) -ForegroundColor Yellow
    }

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
