using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class StopScoringService
{
    private readonly ScoringConfiguration _configuration;

    public StopScoringService()
        : this(new ScoringConfiguration())
    {
    }

    public StopScoringService(ScoringConfiguration configuration)
    {
        _configuration = configuration;
    }

    public StationStopScore ScoreStop(StationInfo station, RealtimeSnapshot snapshot)
    {
        var positionErrorSigned = snapshot.CurrentDistanceMeters - snapshot.TargetStopDistanceMeters;
        var positionError = Math.Abs(positionErrorSigned);
        var scheduledSeconds = snapshot.TimetableHour * 3600 + snapshot.TimetableMinute * 60 + snapshot.TimetableSecond;
        var timeError = Math.Abs(snapshot.MainClockSeconds - scheduledSeconds);

        var positionScore = Math.Max(0, _configuration.MaxScorePerStop - positionError * _configuration.PositionPenaltyPerMeter);
        var timeScore = Math.Max(0, _configuration.MaxScorePerStop - timeError * _configuration.TimePenaltyPerSecond);
        var finalScore = Math.Round(positionScore * _configuration.PositionWeight + timeScore * _configuration.TimeWeight, 1);

        return new StationStopScore
        {
            StationId = station.Id,
            StationName = station.NameJp,
            CapturedAt = snapshot.CapturedAt,
            PositionErrorMeters = Math.Round(positionErrorSigned, 2),
            TimeErrorSeconds = timeError,
            PositionScore = Math.Round(positionScore, 1),
            TimeScore = Math.Round(timeScore, 1),
            FinalScore = finalScore
        };
    }
}
