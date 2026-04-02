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
        LineConfiguration lineConfiguration,
        RealtimeSnapshot snapshot,
        IReadOnlyList<StationInfo> stopStations,
        int visibleTokenCount,
        bool initialized,
        int timelineActiveToken,
        int timelineWindowStart)
    {
        var totalTokens = Math.Max(1, stopStations.Count * 2 - 1);
        var currentPos = ResolveTimelineStationPosition(lineConfiguration, stopStations, snapshot.NextStationId);
        var expectedActiveToken = snapshot.DoorOpen
            ? currentPos * 2
            : Math.Min(totalTokens - 1, currentPos * 2 + 1);

        if (lineConfiguration.LineInfo.IsLoop && initialized && stopStations.Count > 0)
        {
            var lastStationToken = (stopStations.Count - 1) * 2;

            // Keep token progression continuous when wrapping from the last station to the first station.
            if (!snapshot.DoorOpen
                && currentPos == 0
                && timelineActiveToken >= lastStationToken - 1)
            {
                expectedActiveToken = stopStations.Count * 2 - 1;
            }

            // Arrival at the first station after wrap should continue after the closure segment.
            if (snapshot.DoorOpen
                && currentPos == 0
                && timelineActiveToken >= lastStationToken)
            {
                expectedActiveToken = stopStations.Count * 2;
            }
        }

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

        if (lineConfiguration.LineInfo.IsLoop)
        {
            alignedWindowStart = Math.Max(0, alignedWindowStart);
        }
        else
        {
            var maxStart = Math.Max(0, totalTokens - visibleTokenCount);
            alignedWindowStart = Math.Clamp(alignedWindowStart, 0, maxStart);
        }

        return new TimelineState
        {
            TotalTokens = totalTokens,
            ActiveToken = expectedActiveToken,
            WindowStart = alignedWindowStart,
            LastDoorOpen = snapshot.DoorOpen,
            Initialized = true
        };
    }

    private static int ResolveTimelineStationPosition(
        LineConfiguration lineConfiguration,
        IReadOnlyList<StationInfo> stopStations,
        int anchorStationId)
    {
        if (stopStations.Count == 0)
        {
            return 0;
        }

        var stopIndexById = stopStations
            .Select((station, index) => new { station.Id, index })
            .ToDictionary(x => x.Id, x => x.index);

        if (stopIndexById.TryGetValue(anchorStationId, out var exactIndex))
        {
            return exactIndex;
        }

        var allStations = lineConfiguration.Stations;
        var physicalIndex = -1;
        for (var i = 0; i < allStations.Count; i++)
        {
            if (allStations[i].Id == anchorStationId)
            {
                physicalIndex = i;
                break;
            }
        }
        if (physicalIndex < 0)
        {
            return 0;
        }

        for (var i = physicalIndex; i >= 0; i--)
        {
            if (stopIndexById.TryGetValue(allStations[i].Id, out var stopIndex))
            {
                return stopIndex;
            }
        }

        if (lineConfiguration.LineInfo.IsLoop)
        {
            for (var i = allStations.Count - 1; i > physicalIndex; i--)
            {
                if (stopIndexById.TryGetValue(allStations[i].Id, out var stopIndex))
                {
                    return stopIndex;
                }
            }
        }

        return 0;
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
