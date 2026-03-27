namespace JRETS.Go.Core.Runtime;

public sealed class RealtimeSnapshot
{
    public required DateTime CapturedAt { get; init; }

    public required int NextStationId { get; init; }

    public required bool DoorOpen { get; init; }

    public required int MainClockSeconds { get; init; }

    public required int TimetableHour { get; init; }

    public required int TimetableMinute { get; init; }

    public required int TimetableSecond { get; init; }

    public required double CurrentDistanceMeters { get; init; }

    public required double TargetStopDistanceMeters { get; init; }

    public string? LinePath { get; init; }
}
