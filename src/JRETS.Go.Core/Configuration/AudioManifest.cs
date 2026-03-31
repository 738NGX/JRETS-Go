namespace JRETS.Go.Core.Configuration;

public sealed class AudioManifest
{
    public required string Version { get; init; }

    public required IReadOnlyList<AudioManifestEntry> Files { get; init; }

    public IReadOnlyList<string> DeleteFiles { get; init; } = [];

    public string? MinimumClientVersion { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed class AudioManifestEntry
{
    public required string RelativePath { get; init; }

    public required string Sha256 { get; init; }

    public long SizeBytes { get; init; }

    public string? PackageName { get; init; }
}
