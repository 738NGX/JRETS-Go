namespace JRETS.Go.Core.Configuration;

public sealed class MemoryOffsetsConfiguration
{
    public required string ProcessName { get; init; }

    public required string ModuleName { get; init; }

    public required MemoryOffsets Offsets { get; init; }

    /// <summary>
    /// Optional code signatures used to resolve individual fields at attach time.
    /// A configured signature takes precedence over the corresponding static RVA.
    /// </summary>
    public IReadOnlyDictionary<string, MemoryAddressSignature> Signatures { get; init; }
        = new Dictionary<string, MemoryAddressSignature>(StringComparer.Ordinal);
}

public sealed class MemoryAddressSignature
{
    /// <summary>Space-separated bytes; ?? is a wildcard byte.</summary>
    public required string Pattern { get; init; }

    /// <summary>How the address embedded in the matched instruction is decoded.</summary>
    public required MemorySignatureAddressMode AddressMode { get; init; }

    /// <summary>Byte offset of the address/displacement operand within the match.</summary>
    public int OperandOffset { get; init; }

    /// <summary>Byte offset from the match when <see cref="AddressMode"/> is Match.</summary>
    public int MatchOffset { get; init; }

    /// <summary>
    /// Fixed byte offset applied after the address operand is decoded. This lets one
    /// code anchor describe several fields in the same global state block.
    /// </summary>
    public long AddressOffset { get; init; }

    /// <summary>
    /// Optional pointer-chain offsets. For each item, the current address is dereferenced,
    /// then this offset is added.
    /// </summary>
    public IReadOnlyList<long> PointerOffsets { get; init; } = Array.Empty<long>();

    /// <summary>Pointer width of the target process when PointerOffsets is used.</summary>
    public int PointerSize { get; init; } = 4;
}

public enum MemorySignatureAddressMode
{
    Match,
    Absolute32,
    Absolute64,
    Relative32
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
