namespace JRETS.Go.Core.Runtime;

public sealed class DriveSessionReport
{
    public required DateTime StartedAt { get; init; }

    public required DateTime EndedAt { get; init; }

    public required string DataSource { get; init; }

    public required double TotalScore { get; init; }

    public double DistanceMeters { get; init; }

    public DriveSessionMetadata? Metadata { get; init; }

    public required IReadOnlyList<StationStopScore> Stops { get; init; }
}

public sealed class DriveSessionMetadata
{
    public string? TrainNumber { get; init; }

    public string? ServiceType { get; init; }

    public string? LineId { get; init; }

    public string? LineCode { get; init; }

    public string? LineName { get; init; }

    public string? LineColor { get; init; }

    public string? DirectionText { get; init; }

    public string? SegmentText { get; init; }

    public string? OriginStationName { get; init; }
}
