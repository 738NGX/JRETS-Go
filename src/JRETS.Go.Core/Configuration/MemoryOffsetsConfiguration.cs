namespace JRETS.Go.Core.Configuration;

public sealed class MemoryOffsetsConfiguration
{
    public required string ProcessName { get; init; }

    public required string ModuleName { get; init; }

    public required MemoryOffsets Offsets { get; init; }
}

public sealed class MemoryOffsets
{
    public required long NextStationId { get; init; }

    public required long DoorState { get; init; }

    public required long CurrentTimeSeconds { get; init; }

    public required long CurrentTimeMinutes { get; init; }

    public required long CurrentTimeHours { get; init; }

    public required long TimetableSecond { get; init; }

    public required long TimetableMinute { get; init; }

    public required long TimetableHour { get; init; }

    public required long CurrentDistance { get; init; }

    public required long TargetStopDistance { get; init; }

    public long LinePath { get; init; }
}
