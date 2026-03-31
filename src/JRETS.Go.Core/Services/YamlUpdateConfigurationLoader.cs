using JRETS.Go.Core.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JRETS.Go.Core.Services;

public sealed class YamlUpdateConfigurationLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public UpdateConfiguration LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Update config path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Update config file was not found.", filePath);
        }

        var content = File.ReadAllText(filePath);
        var yaml = _deserializer.Deserialize<UpdateConfigurationYaml>(content)
            ?? throw new InvalidOperationException("Update config file is empty.");

        if (yaml.GitHub is null || yaml.App is null || yaml.Configs is null || yaml.Audio is null)
        {
            throw new InvalidOperationException("update.github, update.app, update.configs and update.audio are required.");
        }

        if (string.IsNullOrWhiteSpace(yaml.GitHub.Owner)
            || string.IsNullOrWhiteSpace(yaml.GitHub.Repo))
        {
            throw new InvalidOperationException("update.github.owner and update.github.repo are required.");
        }

        if (string.IsNullOrWhiteSpace(yaml.App.AssetPattern)
            || string.IsNullOrWhiteSpace(yaml.Configs.AssetPattern)
            || string.IsNullOrWhiteSpace(yaml.Audio.AssetPattern)
            || string.IsNullOrWhiteSpace(yaml.Audio.ManifestName)
            || string.IsNullOrWhiteSpace(yaml.Audio.PackagePattern))
        {
            throw new InvalidOperationException("Asset and manifest settings are required for update channels.");
        }

        return new UpdateConfiguration
        {
            GitHub = new GitHubReleaseConfiguration
            {
                Owner = yaml.GitHub.Owner.Trim(),
                Repo = yaml.GitHub.Repo.Trim()
            },
            ReleaseStateAssetName = string.IsNullOrWhiteSpace(yaml.ReleaseStateAssetName)
                ? "release-state.json"
                : yaml.ReleaseStateAssetName.Trim(),
            App = new UpdateChannelConfiguration
            {
                AssetPattern = yaml.App.AssetPattern.Trim(),
                ChecksumName = string.IsNullOrWhiteSpace(yaml.App.ChecksumName)
                    ? "checksums.txt"
                    : yaml.App.ChecksumName.Trim()
            },
            Configs = new UpdateChannelConfiguration
            {
                AssetPattern = yaml.Configs.AssetPattern.Trim(),
                ChecksumName = string.IsNullOrWhiteSpace(yaml.Configs.ChecksumName)
                    ? "checksums.txt"
                    : yaml.Configs.ChecksumName.Trim()
            },
            Audio = new AudioUpdateChannelConfiguration
            {
                AssetPattern = yaml.Audio.AssetPattern.Trim(),
                ChecksumName = string.IsNullOrWhiteSpace(yaml.Audio.ChecksumName)
                    ? "checksums.txt"
                    : yaml.Audio.ChecksumName.Trim(),
                ManifestName = yaml.Audio.ManifestName.Trim(),
                PackagePattern = yaml.Audio.PackagePattern.Trim(),
                RequireFullOnFirstInstall = yaml.Audio.RequireFullOnFirstInstall ?? true
            },
            Mandatory = yaml.Mandatory ?? true,
            CheckTimeoutSeconds = yaml.CheckTimeoutSeconds ?? 20,
            MaxRetryCount = yaml.MaxRetryCount ?? 3,
            IncludePrerelease = yaml.IncludePrerelease ?? false
        };
    }

    private sealed class UpdateConfigurationYaml
    {
        [YamlMember(Alias = "github")]
        public GitHubYaml? GitHub { get; init; }

        [YamlMember(Alias = "release_state_asset_name")]
        public string? ReleaseStateAssetName { get; init; }

        [YamlMember(Alias = "app")]
        public ChannelYaml? App { get; init; }

        [YamlMember(Alias = "configs")]
        public ChannelYaml? Configs { get; init; }

        [YamlMember(Alias = "audio")]
        public AudioChannelYaml? Audio { get; init; }

        [YamlMember(Alias = "mandatory")]
        public bool? Mandatory { get; init; }

        [YamlMember(Alias = "check_timeout_seconds")]
        public int? CheckTimeoutSeconds { get; init; }

        [YamlMember(Alias = "max_retry_count")]
        public int? MaxRetryCount { get; init; }

        [YamlMember(Alias = "include_prerelease")]
        public bool? IncludePrerelease { get; init; }
    }

    private sealed class GitHubYaml
    {
        [YamlMember(Alias = "owner")]
        public string? Owner { get; init; }

        [YamlMember(Alias = "repo")]
        public string? Repo { get; init; }
    }

    private class ChannelYaml
    {
        [YamlMember(Alias = "asset_pattern")]
        public string? AssetPattern { get; init; }

        [YamlMember(Alias = "checksum_name")]
        public string? ChecksumName { get; init; }
    }

    private sealed class AudioChannelYaml : ChannelYaml
    {
        [YamlMember(Alias = "manifest_name")]
        public string? ManifestName { get; init; }

        [YamlMember(Alias = "package_pattern")]
        public string? PackagePattern { get; init; }

        [YamlMember(Alias = "require_full_on_first_install")]
        public bool? RequireFullOnFirstInstall { get; init; }
    }
}
