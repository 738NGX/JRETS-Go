using System;
using System.Collections.Generic;
using System.Linq;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App.Services;

public sealed class TrainRouteService
{
    public string GetDirectionText(LineConfiguration lineConfiguration, TrainInfo? selectedTrain, TrainDisplayState state)
    {
        if (selectedTrain is null)
        {
            return "--";
        }

        if (!lineConfiguration.LineInfo.IsLoop)
        {
            var terminal = lineConfiguration.Stations.FirstOrDefault(x => x.Id == selectedTrain.Terminal);
            return terminal is null ? "--" : $"{terminal.NameJp} 行";
        }

        if (selectedTrain.MajorStationIds is { Count: > 0 })
        {
            return GetLoopLineDirectionByMajorStations(lineConfiguration, selectedTrain, state);
        }

        var fallbackTerminal = lineConfiguration.Stations.FirstOrDefault(x => x.Id == selectedTrain.Terminal);
        return fallbackTerminal is null ? "--" : $"{fallbackTerminal.NameJp} 行";
    }

    public bool IsStopForSelectedService(LineConfiguration lineConfiguration, TrainInfo? selectedTrain, StationInfo station)
    {
        if (selectedTrain is null || string.IsNullOrWhiteSpace(selectedTrain.Id))
        {
            return true;
        }

        if (!IsWithinSelectedServiceRoute(lineConfiguration, selectedTrain, station))
        {
            return false;
        }

        return station.SkipTrain.All(x => !string.Equals(x, selectedTrain.Id, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsWithinSelectedServiceRoute(LineConfiguration lineConfiguration, TrainInfo? selectedTrain, StationInfo station)
    {
        if (selectedTrain is null || lineConfiguration.LineInfo.IsLoop)
        {
            return true;
        }

        var terminalIndex = GetStationIndexInConfiguredOrder(lineConfiguration, selectedTrain.Terminal);
        if (terminalIndex < 0)
        {
            return true;
        }

        var stationIndex = GetStationIndexInConfiguredOrder(lineConfiguration, station.Id);
        if (stationIndex < 0)
        {
            return false;
        }

        return stationIndex <= terminalIndex;
    }

    private static int GetStationIndexInConfiguredOrder(LineConfiguration lineConfiguration, int stationId)
    {
        for (var i = 0; i < lineConfiguration.Stations.Count; i++)
        {
            if (lineConfiguration.Stations[i].Id == stationId)
            {
                return i;
            }
        }

        return -1;
    }

    private static string GetLoopLineDirectionByMajorStations(
        LineConfiguration lineConfiguration,
        TrainInfo selectedTrain,
        TrainDisplayState state)
    {
        var majorStationIds = selectedTrain.MajorStationIds;
        if (majorStationIds is null || majorStationIds.Count == 0)
        {
            return "--";
        }

        var majorStations = majorStationIds
            .Select(id => lineConfiguration.Stations.FirstOrDefault(x => x.Id == id))
            .Where(s => s is not null)
            .Cast<StationInfo>()
            .ToList();

        if (majorStations.Count < 2)
        {
            return "--";
        }

        var majorIndexById = new Dictionary<int, int>();
        for (var i = 0; i < majorStations.Count; i++)
        {
            majorIndexById[majorStations[i].Id] = i;
        }

        var allStations = lineConfiguration.Stations;
        var allStationsList = allStations.ToList();
        if (allStations.Count == 0)
        {
            return "--";
        }

        if (state.CurrentStopStation is not null)
        {
            if (majorIndexById.TryGetValue(state.CurrentStopStation.Id, out var currentMajorIndex))
            {
                var advancedMajorIndex = (currentMajorIndex + 1) % majorStations.Count;
                return BuildMajorDirectionText(majorStations, advancedMajorIndex);
            }

            var currentIndex = allStationsList.FindIndex(x => x.Id == state.CurrentStopStation.Id);
            if (currentIndex < 0)
            {
                return "--";
            }

            var searchStartIndex = (currentIndex + 1) % allStations.Count;
            var nextMajorIndex = FindNextMajorIndexInTravelOrder(allStations, searchStartIndex, majorIndexById);
            return nextMajorIndex < 0 ? "--" : BuildMajorDirectionText(majorStations, nextMajorIndex);
        }

        if (state.NextStation is not null)
        {
            if (majorIndexById.TryGetValue(state.NextStation.Id, out var nextStationMajorIndex))
            {
                var advancedMajorIndex = (nextStationMajorIndex + 1) % majorStations.Count;
                return BuildMajorDirectionText(majorStations, advancedMajorIndex);
            }

            var searchStartIndex = allStationsList.FindIndex(x => x.Id == state.NextStation.Id);
            if (searchStartIndex < 0)
            {
                return "--";
            }

            var nextMajorIndex = FindNextMajorIndexInTravelOrder(allStations, searchStartIndex, majorIndexById);
            return nextMajorIndex < 0 ? "--" : BuildMajorDirectionText(majorStations, nextMajorIndex);
        }

        return "--";
    }

    private static int FindNextMajorIndexInTravelOrder(
        IReadOnlyList<StationInfo> stations,
        int startIndex,
        IReadOnlyDictionary<int, int> majorIndexById)
    {
        for (var i = 0; i < stations.Count; i++)
        {
            var station = stations[(startIndex + i) % stations.Count];
            if (majorIndexById.TryGetValue(station.Id, out var majorIndex))
            {
                return majorIndex;
            }
        }

        return -1;
    }

    private static string BuildMajorDirectionText(IReadOnlyList<StationInfo> majorStations, int majorIndex)
    {
        var nextMajorStation = majorStations[majorIndex];
        var nextNextMajorStation = majorStations[(majorIndex + 1) % majorStations.Count];
        return $"{nextMajorStation.NameJp}·{nextNextMajorStation.NameJp} 方面";
    }
}
