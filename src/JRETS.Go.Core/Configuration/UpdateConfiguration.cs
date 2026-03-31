namespace JRETS.Go.Core.Configuration;

public sealed class UpdateConfiguration
{
    public required GitHubReleaseConfiguration GitHub { get; init; }

    public string ReleaseStateAssetName { get; init; } = "release-state.json";

    public required UpdateChannelConfiguration App { get; init; }

    public required UpdateChannelConfiguration Configs { get; init; }

    public required AudioUpdateChannelConfiguration Audio { get; init; }

    public bool Mandatory { get; init; } = true;

    public int CheckTimeoutSeconds { get; init; } = 20;

    public int MaxRetryCount { get; init; } = 3;

    public bool IncludePrerelease { get; init; } = false;
}

public sealed class GitHubReleaseConfiguration
{
    public required string Owner { get; init; }

    public required string Repo { get; init; }
}

public class UpdateChannelConfiguration
{
    public required string AssetPattern { get; init; }

    public string ChecksumName { get; init; } = "checksums.txt";
}

public sealed class AudioUpdateChannelConfiguration : UpdateChannelConfiguration
{
    public required string ManifestName { get; init; }

    public required string PackagePattern { get; init; }

    public bool RequireFullOnFirstInstall { get; init; } = true;
}
