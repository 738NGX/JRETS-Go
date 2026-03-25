namespace JRETS.Go.Core.Runtime;

public sealed class StationStopScore
{
    public required int StationId { get; init; }

    public required string StationName { get; init; }

    public required DateTime CapturedAt { get; init; }

    public int? ScheduledArrivalSeconds { get; init; }

    public int? ActualArrivalSeconds { get; init; }

    public int? ActualDepartureSeconds { get; init; }

    public double? PositionErrorMeters { get; init; }

    public int? TimeErrorSeconds { get; init; }

    public double? PositionScore { get; init; }

    public double? TimeScore { get; init; }

    public double? FinalScore { get; init; }

    public bool IsScoredStop { get; init; } = true;
}
