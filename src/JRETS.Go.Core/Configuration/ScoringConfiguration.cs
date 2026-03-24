namespace JRETS.Go.Core.Configuration;

public sealed class ScoringConfiguration
{
    public double PositionPenaltyPerMeter { get; init; } = 2.0;

    public double TimePenaltyPerSecond { get; init; } = 1.0;

    public double PositionWeight { get; init; } = 0.6;

    public double TimeWeight { get; init; } = 0.4;

    public double MaxScorePerStop { get; init; } = 100.0;
}
