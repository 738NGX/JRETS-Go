namespace JRETS.Go.Core.Configuration;

public sealed class LineConfiguration
{
    public required LineInfo LineInfo { get; init; }

    public required IReadOnlyList<TrainInfo> TrainInfo { get; init; }

    public required IReadOnlyList<StationInfo> Stations { get; init; }

    public MapInfo? MapInfo { get; init; }
}

public sealed class MapInfo
{
    public required string Stations { get; init; }

    public required string Route { get; init; }
}

public sealed class LineInfo
{
    public string Id { get; init; } = string.Empty;

    public required string NameJp { get; init; }

    public required string NameEn { get; init; }

    public required string LineColor { get; init; }

    public required string Code { get; init; }

    public bool IsLoop { get; init; } = false;
}

public sealed class TrainInfo
{
    public required string Id { get; init; }

    public required string Type { get; init; }

    public required int Terminal { get; init; }

    public List<int>? MajorStationIds { get; init; }

    public int? CcwDirectionStation { get; init; }

    public int? ClockwiseDirectionStation { get; init; }
}

public sealed class StationInfo
{
    public required int Id { get; init; }

    public required int Number { get; init; }

    public required string NameJp { get; init; }

    public required string NameEn { get; init; }

    public string? LineCodeOverride { get; init; }

    public string? LineColorOverride { get; init; }

    public string? Code { get; init; }

    public bool Labeled { get; init; } = false;

    public Dictionary<string, List<PaAnnouncementEntry>> Pa { get; init; } = [];

    public List<string> Melody { get; init; } = [];

    public List<string> SkipTrain { get; init; } = [];
}

public sealed class PaAnnouncementEntry
{
    public required string FileName { get; init; }

    public double? TriggerDistanceMeters { get; init; }
}
