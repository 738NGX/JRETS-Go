using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;

namespace JRETS.Go.App.Services;

public sealed class ReleaseAssetStagingService
{
    private const string VendorDirectoryName = "JRETS.Go.App";
    private readonly HttpClient _httpClient;

    public ReleaseAssetStagingService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<StagedUpdateAssets> DownloadAndVerifyAsync(
        UpdateCheckResult checkResult,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        if (!checkResult.HasUpdate)
        {
            throw new InvalidOperationException("No update channels require download.");
        }

        var stagingDirectory = GetStagingDirectory(checkResult.ReleaseTag);
        Directory.CreateDirectory(stagingDirectory);

        var checksumAsset = checkResult.AssetPlan.ChecksumAsset
            ?? throw new InvalidOperationException("Checksum asset is missing.");

        var requiredAssets = CollectRequiredAssets(checkResult);
        var stagedAssets = new Dictionary<string, StagedAssetReference>(StringComparer.OrdinalIgnoreCase);

        var stagedChecksum = await DownloadAssetAsync(checksumAsset, stagingDirectory, maxRetryCount, cancellationToken)
            .ConfigureAwait(false);

        foreach (var asset in requiredAssets)
        {
            var staged = await DownloadAssetAsync(asset, stagingDirectory, maxRetryCount, cancellationToken)
                .ConfigureAwait(false);
            stagedAssets[asset.Name] = staged;
        }

        var checksums = ParseChecksums(await File.ReadAllTextAsync(stagedChecksum.FilePath, cancellationToken).ConfigureAwait(false));

        foreach (var staged in stagedAssets.Values)
        {
            ValidateSha256(staged, checksums);
        }

        return new StagedUpdateAssets
        {
            StagingDirectory = stagingDirectory,
            AppAsset = ResolveSingleStagedAsset(checkResult.AssetPlan.AppAsset, stagedAssets),
            ConfigsAsset = ResolveSingleStagedAsset(checkResult.AssetPlan.ConfigsAsset, stagedAssets),
            AudioManifestAsset = ResolveSingleStagedAsset(checkResult.AssetPlan.AudioManifestAsset, stagedAssets),
            AudioPackageAssets = checkResult.AssetPlan.AudioPackageAssets
                .Select(x => ResolveSingleStagedAsset(x, stagedAssets))
                .Where(x => x is not null)
                .Cast<StagedAssetReference>()
                .ToList()
        };
    }

    private static IReadOnlyList<RemoteAssetReference> CollectRequiredAssets(UpdateCheckResult checkResult)
    {
        var required = new List<RemoteAssetReference>();

        foreach (var channel in checkResult.Channels.Where(x => x.RequiresUpdate))
        {
            if (channel.Channel == UpdateChannel.App && checkResult.AssetPlan.AppAsset is not null)
            {
                required.Add(checkResult.AssetPlan.AppAsset);
            }

            if (channel.Channel == UpdateChannel.Configs && checkResult.AssetPlan.ConfigsAsset is not null)
            {
                required.Add(checkResult.AssetPlan.ConfigsAsset);
            }

            if (channel.Channel == UpdateChannel.Audio)
            {
                if (checkResult.AssetPlan.AudioManifestAsset is not null)
                {
                    required.Add(checkResult.AssetPlan.AudioManifestAsset);
                }

                required.AddRange(checkResult.AssetPlan.AudioPackageAssets);
            }
        }

        return required
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private async Task<StagedAssetReference> DownloadAssetAsync(
        RemoteAssetReference asset,
        string stagingDirectory,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        var destinationPath = Path.Combine(stagingDirectory, asset.Name);

        for (var attempt = 0; attempt <= maxRetryCount; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync(asset.DownloadUrl, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = File.Create(destinationPath);
                await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

                return new StagedAssetReference
                {
                    Name = asset.Name,
                    FilePath = destinationPath
                };
            }
            catch when (attempt < maxRetryCount)
            {
                await Task.Delay(500 * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException($"Failed to download asset: {asset.Name}");
    }

    private static Dictionary<string, string> ParseChecksums(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            var fileName = parts[^1].TrimStart('*');
            result[fileName] = parts[0].ToLowerInvariant();
        }

        return result;
    }

    private static void ValidateSha256(StagedAssetReference stagedAsset, Dictionary<string, string> checksums)
    {
        if (!checksums.TryGetValue(stagedAsset.Name, out var expected))
        {
            throw new InvalidOperationException($"Missing checksum entry for asset: {stagedAsset.Name}");
        }

        using var stream = File.OpenRead(stagedAsset.FilePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Checksum mismatch for asset: {stagedAsset.Name}");
        }
    }

    private static string GetStagingDirectory(string releaseTag)
    {
        var safeTag = string.Concat(releaseTag.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, VendorDirectoryName, "update-staging", safeTag);
    }

    private static StagedAssetReference? ResolveSingleStagedAsset(
        RemoteAssetReference? source,
        IReadOnlyDictionary<string, StagedAssetReference> staged)
    {
        if (source is null)
        {
            return null;
        }

        return staged.TryGetValue(source.Name, out var value) ? value : null;
    }
}
