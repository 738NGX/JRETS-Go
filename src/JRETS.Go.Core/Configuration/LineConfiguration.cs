namespace JRETS.Go.Core.Configuration;

public sealed class LineConfiguration
{
    public required LineInfo LineInfo { get; init; }

    public required IReadOnlyList<TrainInfo> TrainInfo { get; init; }

    public required IReadOnlyList<StationInfo> Stations { get; init; }
}

public sealed class LineInfo
{
    public required string NameJp { get; init; }

    public required string NameEn { get; init; }

    public required string LineColor { get; init; }

    public required string Code { get; init; }
}

public sealed class TrainInfo
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public required int Terminal { get; init; }
}

public sealed class StationInfo
{
    public required int Id { get; init; }

    public required int Number { get; init; }

    public required string NameJp { get; init; }

    public required string NameEn { get; init; }

    public string? Code { get; init; }

    public List<string> Melody { get; init; } = [];

    public List<string> SkipTrain { get; init; } = [];
}
