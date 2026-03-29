using System;
using System.Collections.Generic;
using System.Linq;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App.Services;

public sealed class TimelineService
{
    public IReadOnlyList<StationInfo> BuildStopStations(
        LineConfiguration lineConfiguration,
        Func<StationInfo, bool> isStopForSelectedService)
    {
        return lineConfiguration.Stations
            .Where(isStopForSelectedService)
            .ToList();
    }

    public TimelineState ComputeState(
        RealtimeSnapshot snapshot,
        IReadOnlyList<StationInfo> stopStations,
        int visibleTokenCount,
        bool initialized,
        int timelineActiveToken,
        int timelineWindowStart)
    {
        var totalTokens = Math.Max(1, stopStations.Count * 2 - 1);
        var currentPos = ResolveTimelineStationPosition(snapshot, stopStations);
        var expectedActiveToken = snapshot.DoorOpen
            ? currentPos * 2
            : Math.Min(totalTokens - 1, currentPos * 2 + 1);

        if (!initialized)
        {
            var initialWindowStart = expectedActiveToken <= 2
                ? 0
                : ((Math.Max(0, expectedActiveToken - 1) / 2) * 2);

            return new TimelineState
            {
                TotalTokens = totalTokens,
                ActiveToken = expectedActiveToken,
                WindowStart = initialWindowStart,
                LastDoorOpen = snapshot.DoorOpen,
                Initialized = true
            };
        }

        var alignedWindowStart = expectedActiveToken <= 2
            ? 0
            : ((Math.Max(0, expectedActiveToken - 1) / 2) * 2);

        var maxStart = Math.Max(0, totalTokens - visibleTokenCount);
        alignedWindowStart = Math.Clamp(alignedWindowStart, 0, maxStart);

        return new TimelineState
        {
            TotalTokens = totalTokens,
            ActiveToken = expectedActiveToken,
            WindowStart = alignedWindowStart,
            LastDoorOpen = snapshot.DoorOpen,
            Initialized = true
        };
    }

    private static int ResolveTimelineStationPosition(RealtimeSnapshot snapshot, IReadOnlyList<StationInfo> stopStations)
    {
        if (stopStations.Count == 0)
        {
            return 0;
        }

        for (var i = 0; i < stopStations.Count; i++)
        {
            if (stopStations[i].Id == snapshot.NextStationId)
            {
                return i;
            }
        }

        var previousIndex = stopStations
            .Select((station, index) => new { station.Id, index })
            .Where(x => x.Id < snapshot.NextStationId)
            .Select(x => x.index)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Clamp(previousIndex, 0, stopStations.Count - 1);
    }
}

public sealed class TimelineState
{
    public required int TotalTokens { get; init; }

    public required int ActiveToken { get; init; }

    public required int WindowStart { get; init; }

    public required bool LastDoorOpen { get; init; }

    public required bool Initialized { get; init; }
}
