namespace JRETS.Go.Core.Configuration;

public sealed class ReleaseStateConfiguration
{
    public required string AppVersion { get; init; }

    public required string ConfigsVersion { get; init; }

    public required string AudioPackVersion { get; init; }
}
