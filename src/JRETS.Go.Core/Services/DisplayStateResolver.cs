using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class DisplayStateResolver
{
    public TrainDisplayState Resolve(LineConfiguration lineConfiguration, RealtimeSnapshot snapshot)
    {
        var stations = lineConfiguration.Stations;
        var currentIndex = Array.FindIndex(stations.ToArray(), x => x.Id == snapshot.NextStationId);

        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var currentStation = stations.Count == 0 ? null : stations[currentIndex];
        var nextStation = currentIndex < stations.Count - 1 ? stations[currentIndex + 1] : null;
        var currentStopStation = snapshot.DoorOpen ? currentStation : null;

        var displayText = snapshot.DoorOpen
            ? currentStation is null ? "Door Open" : $"Stopped: {currentStation.NameJp}"
            : nextStation is null ? "Running" : $"Next: {nextStation.NameJp}";

        var denominator = Math.Max(1, stations.Count - 1);
        var progressIndex = Math.Max(0, currentIndex);
        var progressRatio = Math.Clamp((double)progressIndex / denominator, 0.0, 1.0);

        return new TrainDisplayState
        {
            DoorOpen = snapshot.DoorOpen,
            CurrentStopStation = currentStopStation,
            NextStation = nextStation,
            DisplayText = displayText,
            ProgressRatio = progressRatio
        };
    }
}
