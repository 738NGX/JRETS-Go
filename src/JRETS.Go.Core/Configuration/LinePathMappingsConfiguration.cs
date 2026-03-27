namespace JRETS.Go.Core.Configuration;

public sealed class LinePathMappingsConfiguration
{
    public required IReadOnlyList<LinePathMappingEntry> Paths { get; init; }
}

public sealed class LinePathMappingEntry
{
    public required string Path { get; init; }

    public required string LineId { get; init; }

    public required string TrainId { get; init; }

    public string? DiagramId { get; init; }
}
