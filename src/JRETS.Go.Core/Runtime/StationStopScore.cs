namespace JRETS.Go.Core.Runtime;

public sealed class StationStopScore
{
    public required int StationId { get; init; }

    public required string StationName { get; init; }

    public required DateTime CapturedAt { get; init; }

    public required int ScheduledArrivalSeconds { get; init; }

    public required int ActualArrivalSeconds { get; init; }

    public required double PositionErrorMeters { get; init; }

    public required int TimeErrorSeconds { get; init; }

    public required double PositionScore { get; init; }

    public required double TimeScore { get; init; }

    public required double FinalScore { get; init; }
}
