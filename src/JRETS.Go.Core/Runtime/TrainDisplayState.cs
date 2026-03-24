using JRETS.Go.Core.Configuration;

namespace JRETS.Go.Core.Runtime;

public sealed class TrainDisplayState
{
    public required bool DoorOpen { get; init; }

    public required StationInfo? CurrentStopStation { get; init; }

    public required StationInfo? NextStation { get; init; }

    public required string DisplayText { get; init; }

    public required double ProgressRatio { get; init; }
}
