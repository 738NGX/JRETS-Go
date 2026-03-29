using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class StopScoringService
{
    private const double PerfectPositionToleranceCm = 0.0001;

    private static readonly (double ErrorCm, double Score)[] PositionScoreTable =
    [
        (5, 50),
        (15, 45),
        (30, 40),
        (60, 35),
        (90, 30),
        (120, 25),
        (180, 20),
        (240, 15),
        (300, 10),
        (360, 5),
        (480, 0)
    ];

    private static readonly (double ErrorSeconds, double Score)[] TimeScoreTable =
    [
        (3, 50),
        (6, 45),
        (12, 40),
        (18, 35),
        (24, 30),
        (36, 25),
        (48, 20),
        (60, 15),
        (90, 10),
        (120, 5),
        (180, 0)
    ];

    public StopScoringService()
    {
    }

    public StationStopScore ScoreStop(StationInfo station, RealtimeSnapshot snapshot)
    {
        var positionErrorSigned = snapshot.CurrentDistanceMeters - snapshot.TargetStopDistanceMeters;
        var positionError = Math.Abs(positionErrorSigned);
        var scheduledSeconds = snapshot.TimetableHour * 3600 + snapshot.TimetableMinute * 60 + snapshot.TimetableSecond;
        var timeErrorSigned = snapshot.MainClockSeconds - scheduledSeconds;
        var timeError = Math.Abs(timeErrorSigned);

        var positionErrorCm = positionError * 100;
        var positionScore = StepScoreFromTable(positionErrorCm, PositionScoreTable);
        var timeScore = StepScoreFromTable(timeError, TimeScoreTable);
        var perfectBonus = 0;

        if (positionErrorCm <= PerfectPositionToleranceCm)
        {
            perfectBonus += 10;
        }

        if (timeError == 0)
        {
            perfectBonus += 10;
        }

        var finalScore = Math.Round(positionScore + timeScore + perfectBonus, 1);

        return new StationStopScore
        {
            StationId = station.Id,
            StationName = station.NameJp,
            CapturedAt = snapshot.CapturedAt,
            ScheduledArrivalSeconds = scheduledSeconds,
            ActualArrivalSeconds = snapshot.MainClockSeconds,
            ActualDepartureSeconds = null,
            PositionErrorMeters = Math.Round(positionErrorSigned, 2),
            TimeErrorSeconds = timeErrorSigned,
            PositionScore = Math.Round(positionScore, 1),
            TimeScore = Math.Round(timeScore, 1),
            FinalScore = finalScore,
            IsScoredStop = true
        };
    }

    private static double StepScoreFromTable(double errorValue, IReadOnlyList<(double ErrorThreshold, double Score)> table)
    {
        for (var i = 0; i < table.Count; i++)
        {
            var tier = table[i];
            if (errorValue > tier.ErrorThreshold)
            {
                continue;
            }

            return tier.Score;
        }

        return table[^1].Score;
    }
}
