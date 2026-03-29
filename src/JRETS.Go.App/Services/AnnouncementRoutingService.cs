using System;
using System.Collections.Generic;
using System.Linq;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App.Services;

public sealed class AnnouncementRoutingService
{
    public int? ResolveAnnouncementTargetStationId(
        LineConfiguration lineConfiguration,
        TrainDisplayState state,
        Func<StationInfo, bool> isWithinSelectedServiceRoute,
        Func<StationInfo, bool> isStopForSelectedService)
    {
        var orderedStations = lineConfiguration.Stations
            .Where(isWithinSelectedServiceRoute)
            .ToList();
        if (orderedStations.Count == 0)
        {
            return null;
        }

        var anchorIndex = state.CurrentStopStation is not null
            ? orderedStations.FindIndex(x => x.Id == state.CurrentStopStation.Id) + 1
            : state.NextStation is not null
                ? orderedStations.FindIndex(x => x.Id == state.NextStation.Id)
                : -1;

        if (anchorIndex < 0)
        {
            return null;
        }

        for (var i = anchorIndex; i < orderedStations.Count; i++)
        {
            if (isStopForSelectedService(orderedStations[i]))
            {
                return NormalizeStationIdForScoring(lineConfiguration, orderedStations[i].Id);
            }
        }

        if (!lineConfiguration.LineInfo.IsLoop)
        {
            return null;
        }

        for (var i = 0; i < anchorIndex && i < orderedStations.Count; i++)
        {
            if (isStopForSelectedService(orderedStations[i]))
            {
                return NormalizeStationIdForScoring(lineConfiguration, orderedStations[i].Id);
            }
        }

        return null;
    }

    public int NormalizeStationIdForScoring(LineConfiguration lineConfiguration, int stationId)
    {
        var stations = lineConfiguration.Stations;
        var station = stations.FirstOrDefault(s => s.Id == stationId);
        if (station is null)
        {
            return stationId;
        }

        for (var i = 0; i < stations.Count; i++)
        {
            var candidate = stations[i];
            if (!string.Equals(candidate.NameJp, station.NameJp, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.Number != station.Number)
            {
                continue;
            }

            var candidateCode = candidate.Code ?? string.Empty;
            var stationCode = station.Code ?? string.Empty;
            if (!string.Equals(candidateCode, stationCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate.Id;
        }

        return stationId;
    }

    public int? ResolveNextStoppingStationFromCurrentId(
        LineConfiguration lineConfiguration,
        int? currentStationId,
        Func<StationInfo, bool> isStopForSelectedService)
    {
        if (!currentStationId.HasValue)
        {
            return null;
        }

        var allStations = lineConfiguration.Stations.ToList();
        var currentIndex = allStations.FindIndex(s => s.Id == currentStationId.Value);
        if (currentIndex < 0)
        {
            return null;
        }

        for (var i = currentIndex + 1; i < allStations.Count; i++)
        {
            if (isStopForSelectedService(allStations[i]))
            {
                return NormalizeStationIdForScoring(lineConfiguration, allStations[i].Id);
            }
        }

        if (lineConfiguration.LineInfo.IsLoop)
        {
            for (var i = 0; i < currentIndex; i++)
            {
                if (isStopForSelectedService(allStations[i]))
                {
                    return NormalizeStationIdForScoring(lineConfiguration, allStations[i].Id);
                }
            }
        }

        return null;
    }
}
