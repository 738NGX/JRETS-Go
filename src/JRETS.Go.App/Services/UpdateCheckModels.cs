using JRETS.Go.Core.Configuration;

namespace JRETS.Go.App.Services;

public enum UpdateChannel
{
    App,
    Configs,
    Audio
}

public sealed class LocalReleaseState
{
    public required string AppVersion { get; init; }

    public required string ConfigsVersion { get; init; }

    public required string AudioPackVersion { get; init; }
}

public sealed class UpdateChannelCheckResult
{
    public required UpdateChannel Channel { get; init; }

    public required string LocalVersion { get; init; }

    public required string TargetVersion { get; init; }

    public required bool RequiresUpdate { get; init; }
}

public sealed class UpdateCheckResult
{
    public required string ReleaseTag { get; init; }

    public required ReleaseStateConfiguration TargetState { get; init; }

    public required UpdateAssetPlan AssetPlan { get; init; }

    public required IReadOnlyList<UpdateChannelCheckResult> Channels { get; init; }

    public bool HasUpdate => Channels.Any(x => x.RequiresUpdate);
}

public sealed class UpdateAssetPlan
{
    public RemoteAssetReference? ChecksumAsset { get; init; }

    public RemoteAssetReference? AppAsset { get; init; }

    public RemoteAssetReference? ConfigsAsset { get; init; }

    public RemoteAssetReference? AudioManifestAsset { get; init; }

    public IReadOnlyList<RemoteAssetReference> AudioPackageAssets { get; init; } = [];
}

public sealed class RemoteAssetReference
{
    public required string Name { get; init; }

    public required string DownloadUrl { get; init; }
}

public sealed class StagedAssetReference
{
    public required string Name { get; init; }

    public required string FilePath { get; init; }
}

public sealed class StagedUpdateAssets
{
    public required string StagingDirectory { get; init; }

    public StagedAssetReference? AppAsset { get; init; }

    public StagedAssetReference? ConfigsAsset { get; init; }

    public StagedAssetReference? AudioManifestAsset { get; init; }

    public IReadOnlyList<StagedAssetReference> AudioPackageAssets { get; init; } = [];
}
