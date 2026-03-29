using System;
using System.Collections.Generic;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App.Services;

public sealed class StationScoreService
{
    public void UpsertDepartureRecord(
        IList<StationStopScore> stationScores,
        int stationId,
        string stationName,
        DateTime capturedAt,
        int departureSeconds)
    {
        for (var i = stationScores.Count - 1; i >= 0; i--)
        {
            var row = stationScores[i];
            if (row.StationId != stationId)
            {
                continue;
            }

            if (!row.IsScoredStop || !row.ActualDepartureSeconds.HasValue)
            {
                stationScores[i] = CloneWithDeparture(row, departureSeconds);
                return;
            }
        }

        stationScores.Add(new StationStopScore
        {
            StationId = stationId,
            StationName = stationName,
            CapturedAt = capturedAt,
            ScheduledArrivalSeconds = null,
            ActualArrivalSeconds = null,
            ActualDepartureSeconds = departureSeconds,
            PositionErrorMeters = null,
            TimeErrorSeconds = null,
            PositionScore = null,
            TimeScore = null,
            FinalScore = null,
            IsScoredStop = false
        });
    }

    public RealtimeSnapshot BuildScoringSnapshot(
        RealtimeSnapshot currentSnapshot,
        double? activeApproachTargetStopDistance,
        int? activeApproachScheduledSeconds,
        bool activeApproachOvershootFaultTriggered,
        double overshootFaultMeters)
    {
        if (activeApproachTargetStopDistance is null || activeApproachScheduledSeconds is null)
        {
            return currentSnapshot;
        }

        var scheduledSeconds = activeApproachScheduledSeconds.Value;
        var scheduledHour = scheduledSeconds / 3600;
        var scheduledMinute = (scheduledSeconds % 3600) / 60;
        var scheduledSecond = scheduledSeconds % 60;
        var targetStopDistance = activeApproachTargetStopDistance.Value;

        var adjustedCurrentDistance = currentSnapshot.CurrentDistanceMeters;
        var currentPositionErrorSigned = adjustedCurrentDistance - targetStopDistance;
        if (activeApproachOvershootFaultTriggered && currentPositionErrorSigned < overshootFaultMeters)
        {
            adjustedCurrentDistance = targetStopDistance + overshootFaultMeters;
        }

        return new RealtimeSnapshot
        {
            CapturedAt = currentSnapshot.CapturedAt,
            NextStationId = currentSnapshot.NextStationId,
            DoorOpen = currentSnapshot.DoorOpen,
            MainClockSeconds = currentSnapshot.MainClockSeconds,
            TimetableHour = scheduledHour,
            TimetableMinute = scheduledMinute,
            TimetableSecond = scheduledSecond,
            CurrentDistanceMeters = adjustedCurrentDistance,
            TargetStopDistanceMeters = targetStopDistance,
            LinePath = currentSnapshot.LinePath
        };
    }

    private static StationStopScore CloneWithDeparture(StationStopScore row, int departureSeconds)
    {
        return new StationStopScore
        {
            StationId = row.StationId,
            StationName = row.StationName,
            CapturedAt = row.CapturedAt,
            ScheduledArrivalSeconds = row.ScheduledArrivalSeconds,
            ActualArrivalSeconds = row.ActualArrivalSeconds,
            ActualDepartureSeconds = departureSeconds,
            PositionErrorMeters = row.PositionErrorMeters,
            TimeErrorSeconds = row.TimeErrorSeconds,
            PositionScore = row.PositionScore,
            TimeScore = row.TimeScore,
            FinalScore = row.FinalScore,
            IsScoredStop = row.IsScoredStop
        };
    }
}
