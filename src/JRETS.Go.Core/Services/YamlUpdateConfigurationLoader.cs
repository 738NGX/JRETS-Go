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
        public GitHubYaml? GitHub { get; init; }

        public string? ReleaseStateAssetName { get; init; }

        public ChannelYaml? App { get; init; }

        public ChannelYaml? Configs { get; init; }

        public AudioChannelYaml? Audio { get; init; }

        public bool? Mandatory { get; init; }

        public int? CheckTimeoutSeconds { get; init; }

        public int? MaxRetryCount { get; init; }

        public bool? IncludePrerelease { get; init; }
    }

    private sealed class GitHubYaml
    {
        public string? Owner { get; init; }

        public string? Repo { get; init; }
    }

    private class ChannelYaml
    {
        public string? AssetPattern { get; init; }

        public string? ChecksumName { get; init; }
    }

    private sealed class AudioChannelYaml : ChannelYaml
    {
        public string? ManifestName { get; init; }

        public string? PackagePattern { get; init; }

        public bool? RequireFullOnFirstInstall { get; init; }
    }
}
