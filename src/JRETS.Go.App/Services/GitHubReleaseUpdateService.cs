using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using JRETS.Go.Core.Configuration;

namespace JRETS.Go.App.Services;

public sealed class GitHubReleaseUpdateService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public GitHubReleaseUpdateService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? CreateDefaultHttpClient();
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateConfiguration configuration,
        LocalReleaseState localState,
        CancellationToken cancellationToken)
    {
        var release = await GetLatestReleaseAsync(configuration, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("No applicable release found on GitHub.");

        var releaseStateAsset = release.Assets.FirstOrDefault(x =>
            string.Equals(x.Name, configuration.ReleaseStateAssetName, StringComparison.OrdinalIgnoreCase));

        if (releaseStateAsset is null || string.IsNullOrWhiteSpace(releaseStateAsset.BrowserDownloadUrl))
        {
            throw new InvalidOperationException(
                $"Release asset '{configuration.ReleaseStateAssetName}' was not found in tag {release.TagName}.");
        }

        var targetState = await DownloadReleaseStateAsync(releaseStateAsset.BrowserDownloadUrl, cancellationToken)
            .ConfigureAwait(false);

        var channelResults = new[]
        {
            BuildChannelResult(UpdateChannel.App, localState.AppVersion, targetState.AppVersion),
            BuildChannelResult(UpdateChannel.Configs, localState.ConfigsVersion, targetState.ConfigsVersion),
            BuildChannelResult(UpdateChannel.Audio, localState.AudioPackVersion, targetState.AudioPackVersion)
        };

        var assetPlan = BuildAssetPlan(release.Assets, configuration);
        ValidateRequiredAssets(channelResults, assetPlan);

        return new UpdateCheckResult
        {
            ReleaseTag = release.TagName,
            TargetState = targetState,
            AssetPlan = assetPlan,
            Channels = channelResults
        };
    }

    private static UpdateAssetPlan BuildAssetPlan(
        IReadOnlyList<GitHubAssetDto> assets,
        UpdateConfiguration configuration)
    {
        return new UpdateAssetPlan
        {
            ChecksumAsset = FindExactAsset(assets, configuration.App.ChecksumName)
                ?? FindExactAsset(assets, configuration.Configs.ChecksumName)
                ?? FindExactAsset(assets, configuration.Audio.ChecksumName),
            AppAsset = FindAssetByPattern(assets, configuration.App.AssetPattern),
            ConfigsAsset = FindAssetByPattern(assets, configuration.Configs.AssetPattern),
            AudioManifestAsset = FindExactAsset(assets, configuration.Audio.ManifestName),
            AudioPackageAssets = FindAssetsByPattern(assets, configuration.Audio.PackagePattern)
        };
    }

    private static void ValidateRequiredAssets(
        IReadOnlyList<UpdateChannelCheckResult> channelResults,
        UpdateAssetPlan assetPlan)
    {
        foreach (var channel in channelResults)
        {
            if (!channel.RequiresUpdate)
            {
                continue;
            }

            if (channel.Channel == UpdateChannel.App && assetPlan.AppAsset is null)
            {
                throw new InvalidOperationException("Required app asset is missing for update.");
            }

            if (channel.Channel == UpdateChannel.Configs && assetPlan.ConfigsAsset is null)
            {
                throw new InvalidOperationException("Required configs asset is missing for update.");
            }

            if (channel.Channel == UpdateChannel.Audio
                && (assetPlan.AudioManifestAsset is null || assetPlan.AudioPackageAssets.Count == 0))
            {
                throw new InvalidOperationException("Required audio assets are missing for update.");
            }
        }

        if (channelResults.Any(x => x.RequiresUpdate) && assetPlan.ChecksumAsset is null)
        {
            throw new InvalidOperationException("Checksum asset is required when updates are available.");
        }
    }

    private static RemoteAssetReference? FindExactAsset(IReadOnlyList<GitHubAssetDto> assets, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var matched = assets.FirstOrDefault(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));

        return matched is null || string.IsNullOrWhiteSpace(matched.BrowserDownloadUrl)
            ? null
            : new RemoteAssetReference
            {
                Name = matched.Name,
                DownloadUrl = matched.BrowserDownloadUrl
            };
    }

    private static RemoteAssetReference? FindAssetByPattern(IReadOnlyList<GitHubAssetDto> assets, string pattern)
    {
        return FindAssetsByPattern(assets, pattern).FirstOrDefault();
    }

    private static IReadOnlyList<RemoteAssetReference> FindAssetsByPattern(IReadOnlyList<GitHubAssetDto> assets, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return [];
        }

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        var regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return assets
            .Where(x => !string.IsNullOrWhiteSpace(x.BrowserDownloadUrl) && regex.IsMatch(x.Name))
            .Select(x => new RemoteAssetReference
            {
                Name = x.Name,
                DownloadUrl = x.BrowserDownloadUrl
            })
            .ToList();
    }

    private async Task<ReleaseStateConfiguration> DownloadReleaseStateAsync(
        string url,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var state = JsonSerializer.Deserialize<ReleaseStateConfiguration>(content, JsonOptions)
            ?? throw new InvalidOperationException("release-state.json is empty or malformed.");

        if (string.IsNullOrWhiteSpace(state.AppVersion)
            || string.IsNullOrWhiteSpace(state.ConfigsVersion)
            || string.IsNullOrWhiteSpace(state.AudioPackVersion))
        {
            throw new InvalidOperationException("release-state.json requires appVersion, configsVersion and audioPackVersion.");
        }

        return state;
    }

    private async Task<GitHubReleaseDto?> GetLatestReleaseAsync(
        UpdateConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.github.com/repos/{configuration.GitHub.Owner}/{configuration.GitHub.Repo}/releases";
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var releases = JsonSerializer.Deserialize<List<GitHubReleaseDto>>(content, JsonOptions) ?? [];

        return releases.FirstOrDefault(x =>
            !x.Draft && (configuration.IncludePrerelease || !x.Prerelease));
    }

    private static UpdateChannelCheckResult BuildChannelResult(UpdateChannel channel, string local, string target)
    {
        var localVersion = ParseVersion(local);
        var targetVersion = ParseVersion(target);

        return new UpdateChannelCheckResult
        {
            Channel = channel,
            LocalVersion = local,
            TargetVersion = target,
            RequiresUpdate = targetVersion > localVersion
        };
    }

    private static Version ParseVersion(string rawVersion)
    {
        var normalized = rawVersion.Trim();
        if (normalized.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[1..];
        }

        var prereleaseIndex = normalized.IndexOf('-');
        if (prereleaseIndex >= 0)
        {
            normalized = normalized[..prereleaseIndex];
        }

        if (!Version.TryParse(normalized, out var parsed))
        {
            return new Version(0, 0, 0);
        }

        return parsed.Build < 0
            ? new Version(parsed.Major, parsed.Minor, 0)
            : parsed;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("JRETS-Go", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto> Assets { get; init; } = [];
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
