using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JRETS.Go.Core.Configuration;

namespace JRETS.Go.App.Services;

public sealed class ChannelUpdateApplyService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LocalReleaseState> ApplyNonAppChannelsAsync(
        UpdateCheckResult checkResult,
        StagedUpdateAssets stagedAssets,
        LocalReleaseState currentState,
        string appBaseDirectory,
        CancellationToken cancellationToken)
    {
        var nextAppVersion = currentState.AppVersion;
        var nextConfigsVersion = currentState.ConfigsVersion;
        var nextAudioVersion = currentState.AudioPackVersion;

        var requiresConfigs = checkResult.Channels.Any(x => x.Channel == UpdateChannel.Configs && x.RequiresUpdate);
        if (requiresConfigs)
        {
            if (stagedAssets.ConfigsAsset is null)
            {
                throw new InvalidOperationException("Configs asset is not staged.");
            }

            ApplyConfigsAsset(stagedAssets.ConfigsAsset, appBaseDirectory);
            nextConfigsVersion = checkResult.TargetState.ConfigsVersion;
        }

        var requiresAudio = checkResult.Channels.Any(x => x.Channel == UpdateChannel.Audio && x.RequiresUpdate);
        if (requiresAudio)
        {
            if (stagedAssets.AudioManifestAsset is null)
            {
                throw new InvalidOperationException("Audio manifest asset is not staged.");
            }

            await ApplyAudioAssetsAsync(
                stagedAssets.AudioManifestAsset,
                stagedAssets.AudioPackageAssets,
                appBaseDirectory,
                cancellationToken).ConfigureAwait(false);

            nextAudioVersion = checkResult.TargetState.AudioPackVersion;
        }

        return new LocalReleaseState
        {
            AppVersion = nextAppVersion,
            ConfigsVersion = nextConfigsVersion,
            AudioPackVersion = nextAudioVersion
        };
    }

    private static void ApplyConfigsAsset(StagedAssetReference configsAsset, string appBaseDirectory)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "JRETS.Go.App", "config-apply", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            ZipFile.ExtractToDirectory(configsAsset.FilePath, tempDirectory, overwriteFiles: true);

            var extractedConfigsRoot = Directory.Exists(Path.Combine(tempDirectory, "configs"))
                ? Path.Combine(tempDirectory, "configs")
                : tempDirectory;

            var targetConfigsRoot = Path.Combine(appBaseDirectory, "configs");
            CopyDirectory(extractedConfigsRoot, targetConfigsRoot);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static async Task ApplyAudioAssetsAsync(
        StagedAssetReference manifestAsset,
        IReadOnlyList<StagedAssetReference> packageAssets,
        string appBaseDirectory,
        CancellationToken cancellationToken)
    {
        var manifestContent = await File.ReadAllTextAsync(manifestAsset.FilePath, cancellationToken).ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize<AudioManifest>(manifestContent, JsonOptions)
            ?? throw new InvalidOperationException("audio-manifest.json is empty or malformed.");

        var extractDirectory = Path.Combine(Path.GetTempPath(), "JRETS.Go.App", "audio-apply", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractDirectory);

        try
        {
            foreach (var package in packageAssets)
            {
                ZipFile.ExtractToDirectory(package.FilePath, extractDirectory, overwriteFiles: true);
            }

            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sourcePath = ResolveExtractedAudioSourcePath(extractDirectory, file.RelativePath);
                if (sourcePath is null)
                {
                    throw new InvalidOperationException($"Audio file '{file.RelativePath}' was not found in staged packages.");
                }

                var targetPath = ResolveAudioTargetPath(appBaseDirectory, file.RelativePath);
                var targetDirectory = Path.GetDirectoryName(targetPath)
                    ?? throw new InvalidOperationException("Audio target directory could not be resolved.");

                Directory.CreateDirectory(targetDirectory);
                File.Copy(sourcePath, targetPath, overwrite: true);

                ValidateFileHash(targetPath, file.Sha256);
            }

            foreach (var deletePath in manifest.DeleteFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targetDeletePath = ResolveAudioTargetPath(appBaseDirectory, deletePath);
                if (File.Exists(targetDeletePath))
                {
                    File.Delete(targetDeletePath);
                }
            }
        }
        finally
        {
            if (Directory.Exists(extractDirectory))
            {
                Directory.Delete(extractDirectory, recursive: true);
            }
        }
    }

    private static string? ResolveExtractedAudioSourcePath(string extractDirectory, string relativePath)
    {
        var normalizedRelative = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        var directPath = Path.Combine(extractDirectory, normalizedRelative);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        var audioPrefixedPath = Path.Combine(extractDirectory, "audio", normalizedRelative);
        if (File.Exists(audioPrefixedPath))
        {
            return audioPrefixedPath;
        }

        return null;
    }

    private static string ResolveAudioTargetPath(string appBaseDirectory, string relativePath)
    {
        var normalizedRelative = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

        if (normalizedRelative.StartsWith("audio" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(appBaseDirectory, normalizedRelative);
        }

        return Path.Combine(appBaseDirectory, "audio", normalizedRelative);
    }

    private static void ValidateFileHash(string filePath, string expectedSha256)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var actual = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
        var expected = expectedSha256.Trim().ToLowerInvariant();

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Audio hash validation failed for '{filePath}'.");
        }
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relative);
            var targetFileDirectory = Path.GetDirectoryName(targetFile)
                ?? throw new InvalidOperationException("Target file directory could not be resolved.");

            Directory.CreateDirectory(targetFileDirectory);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }
}