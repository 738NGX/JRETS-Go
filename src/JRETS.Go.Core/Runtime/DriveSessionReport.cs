namespace JRETS.Go.Core.Runtime;

public sealed class DriveSessionReport
{
    public required DateTime StartedAt { get; init; }

    public required DateTime EndedAt { get; init; }

    public required string DataSource { get; init; }

    public required double TotalScore { get; init; }

    public required IReadOnlyList<StationStopScore> Stops { get; init; }
}
