using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class DisplayStateResolver
{
    public TrainDisplayState Resolve(
        LineConfiguration lineConfiguration,
        RealtimeSnapshot snapshot,
        Func<StationInfo, bool> isStopForSelectedService,
        int? latchedStationId)
    {
        var stopStations = lineConfiguration.Stations
            .Where(isStopForSelectedService)
            .ToList();
        if (stopStations.Count == 0)
        {
            return new TrainDisplayState
            {
                DoorOpen = snapshot.DoorOpen,
                CurrentStopStation = null,
                NextStation = null,
                DisplayText = snapshot.DoorOpen ? "Door Open" : "Running",
                ProgressRatio = 0
            };
        }

        var anchorStationId = snapshot.DoorOpen
            ? latchedStationId ?? snapshot.NextStationId
            : snapshot.NextStationId;

        var currentIndex = ResolveCurrentStopIndex(lineConfiguration, stopStations, anchorStationId);
        var currentStation = stopStations[currentIndex];
        var nextStation = ResolveNextStopStation(lineConfiguration, stopStations, currentIndex);

        var displayText = snapshot.DoorOpen
            ? currentStation is null ? "Door Open" : $"Stopped: {currentStation.NameJp}"
            : nextStation is null ? "Running" : $"Next: {nextStation.NameJp}";

        var denominator = Math.Max(1, stopStations.Count - 1);
        var progressIndex = Math.Max(0, currentIndex);
        var progressRatio = Math.Clamp((double)progressIndex / denominator, 0.0, 1.0);

        return new TrainDisplayState
        {
            DoorOpen = snapshot.DoorOpen,
            CurrentStopStation = currentStation,
            NextStation = nextStation,
            DisplayText = displayText,
            ProgressRatio = progressRatio
        };
    }

    private static int ResolveCurrentStopIndex(LineConfiguration lineConfiguration, IReadOnlyList<StationInfo> stopStations, int anchorStationId)
    {
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

    private static StationInfo? ResolveNextStopStation(
        LineConfiguration lineConfiguration,
        IReadOnlyList<StationInfo> stopStations,
        int currentIndex)
    {
        if (stopStations.Count == 0)
        {
            return null;
        }

        if (currentIndex < stopStations.Count - 1)
        {
            return stopStations[currentIndex + 1];
        }

        return lineConfiguration.LineInfo.IsLoop ? stopStations[0] : null;
    }
}
