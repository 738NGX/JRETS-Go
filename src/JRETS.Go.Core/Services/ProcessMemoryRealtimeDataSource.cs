using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class ProcessMemoryRealtimeDataSource : IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const int MergeAdjacentGapBytes = 64;
    private const int LinePathReadLength = 1024;

    private readonly MemoryOffsetsConfiguration _configuration;
    private SnapshotReadPlan _snapshotReadPlan;
    private SnapshotReadPlan _snapshotReadPlanWithoutStationId;

    private Process? _process;
    private nint _processHandle;
    private nint _moduleBaseAddress;
    private int _moduleMemorySize;
    private MemoryOffsets _resolvedOffsets;

    public string LastAttachError { get; private set; } = string.Empty;

    public ProcessMemoryRealtimeDataSource(MemoryOffsetsConfiguration configuration)
    {
        _configuration = configuration;
        _snapshotReadPlan = BuildSnapshotReadPlan(configuration.Offsets);
        _snapshotReadPlanWithoutStationId = BuildSnapshotReadPlanWithoutStationId(configuration.Offsets);
        _resolvedOffsets = configuration.Offsets;
    }

    public bool TryAttach()
    {
        Release();

        var processName = Path.GetFileNameWithoutExtension(_configuration.ProcessName);
        var process = Process.GetProcessesByName(processName).FirstOrDefault();
        if (process is null)
        {
            LastAttachError = $"Process '{_configuration.ProcessName}' not found.";
            return false;
        }

        nint moduleBaseAddress;
        int moduleMemorySize;
        try
        {
            var module = process.Modules.Cast<ProcessModule>()
                .FirstOrDefault(m => string.Equals(m.ModuleName, _configuration.ModuleName, StringComparison.OrdinalIgnoreCase));
            if (module is null)
            {
                LastAttachError = $"Module '{_configuration.ModuleName}' not found.";
                return false;
            }

            moduleBaseAddress = module.BaseAddress;
            moduleMemorySize = module.ModuleMemorySize;
        }
        catch (Exception ex)
        {
            LastAttachError = $"Cannot inspect process modules: {ex.Message}";
            return false;
        }

        var handle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, process.Id);
        if (handle == nint.Zero)
        {
            LastAttachError = "OpenProcess failed. Try running as administrator.";
            return false;
        }

        _process = process;
        _processHandle = handle;
        _moduleBaseAddress = moduleBaseAddress;
        _moduleMemorySize = moduleMemorySize;

        try
        {
            _resolvedOffsets = ResolveOffsets();
            _snapshotReadPlan = BuildSnapshotReadPlan(_resolvedOffsets);
            _snapshotReadPlanWithoutStationId = BuildSnapshotReadPlanWithoutStationId(_resolvedOffsets);
        }
        catch (Exception ex)
        {
            LastAttachError = $"Failed to resolve memory locations: {ex.Message}";
            Release();
            return false;
        }

        LastAttachError = string.Empty;
        return true;
    }

    public RealtimeSnapshot GetSnapshot()
    {
        if ((_process is null || _process.HasExited || _processHandle == nint.Zero) && !TryAttach())
        {
            throw new InvalidOperationException(LastAttachError);
        }

        var values = ReadSnapshotValues(_snapshotReadPlan, includeStationId: true);
        var mainClockSeconds = (values.CurrentTimeHours * 3600)
            + (values.CurrentTimeMinutes * 60)
            + values.CurrentTimeSeconds;

        return new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = values.NextStationId,
            // Game door state is non-binary on some lines (e.g. transient 44 when opening).
            // Treat any non-zero value as "door open" at snapshot level.
            DoorOpen = values.DoorState != 0,
            MainClockSeconds = mainClockSeconds,
            TimetableHour = values.TimetableHour,
            TimetableMinute = values.TimetableMinute,
            TimetableSecond = values.TimetableSecond,
            CurrentDistanceMeters = values.CurrentDistanceMeters,
            TargetStopDistanceMeters = values.TargetStopDistanceMeters,
            LinePath = values.LinePath
        };
    }

    public RealtimeSnapshot GetSnapshotWithoutStationId(int fallbackStationId)
    {
        if ((_process is null || _process.HasExited || _processHandle == nint.Zero) && !TryAttach())
        {
            throw new InvalidOperationException(LastAttachError);
        }

        var values = ReadSnapshotValues(_snapshotReadPlanWithoutStationId, includeStationId: false);
        var mainClockSeconds = (values.CurrentTimeHours * 3600)
            + (values.CurrentTimeMinutes * 60)
            + values.CurrentTimeSeconds;

        return new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = fallbackStationId,
            // Game door state is non-binary on some lines (e.g. transient 44 when opening).
            // Treat any non-zero value as "door open" at snapshot level.
            DoorOpen = values.DoorState != 0,
            MainClockSeconds = mainClockSeconds,
            TimetableHour = values.TimetableHour,
            TimetableMinute = values.TimetableMinute,
            TimetableSecond = values.TimetableSecond,
            CurrentDistanceMeters = values.CurrentDistanceMeters,
            TargetStopDistanceMeters = values.TargetStopDistanceMeters,
            LinePath = values.LinePath
        };
    }

    private SnapshotValues ReadSnapshotValues(SnapshotReadPlan readPlan, bool includeStationId)
    {
        if (_processHandle == nint.Zero)
        {
            throw new InvalidOperationException("Process is not attached.");
        }

        var segmentBuffers = new byte[readPlan.Segments.Count][];
        for (var i = 0; i < readPlan.Segments.Count; i++)
        {
            var segment = readPlan.Segments[i];
            segmentBuffers[i] = ReadBytes(segment.StartOffset, segment.Size);
        }

        return new SnapshotValues
        {
            NextStationId = includeStationId && readPlan.NextStationIdField is FieldReadInfo nextStationField
                ? ReadInt32(segmentBuffers, nextStationField)
                : 0,
            DoorState = ReadByte(segmentBuffers, readPlan.DoorStateField),
            CurrentTimeSeconds = ReadInt32(segmentBuffers, readPlan.CurrentTimeSecondsField),
            CurrentTimeMinutes = ReadInt32(segmentBuffers, readPlan.CurrentTimeMinutesField),
            CurrentTimeHours = ReadInt32(segmentBuffers, readPlan.CurrentTimeHoursField),
            TimetableSecond = ReadInt32(segmentBuffers, readPlan.TimetableSecondField),
            TimetableMinute = ReadInt32(segmentBuffers, readPlan.TimetableMinuteField),
            TimetableHour = ReadInt32(segmentBuffers, readPlan.TimetableHourField),
            CurrentDistanceMeters = ReadDouble(segmentBuffers, readPlan.CurrentDistanceField),
            TargetStopDistanceMeters = ReadDouble(segmentBuffers, readPlan.TargetStopDistanceField),
            LinePath = ReadLinePath(_resolvedOffsets.LinePath)
        };
    }

    private MemoryOffsets ResolveOffsets()
    {
        if (_configuration.Signatures.Count == 0)
        {
            return _configuration.Offsets;
        }

        if (_moduleMemorySize <= 0)
        {
            throw new InvalidOperationException("Target module has no readable memory image.");
        }

        var moduleBytes = ReadBytes(0, _moduleMemorySize);
        var offsets = _configuration.Offsets;

        return new MemoryOffsets
        {
            NextStationId = ResolveOffset("next_station_id", offsets.NextStationId, moduleBytes),
            DoorState = ResolveOffset("door_state", offsets.DoorState, moduleBytes),
            CurrentTimeSeconds = ResolveOffset("current_time_seconds", offsets.CurrentTimeSeconds, moduleBytes),
            CurrentTimeMinutes = ResolveOffset("current_time_minutes", offsets.CurrentTimeMinutes, moduleBytes),
            CurrentTimeHours = ResolveOffset("current_time_hours", offsets.CurrentTimeHours, moduleBytes),
            TimetableSecond = ResolveOffset("timetable_second", offsets.TimetableSecond, moduleBytes),
            TimetableMinute = ResolveOffset("timetable_minute", offsets.TimetableMinute, moduleBytes),
            TimetableHour = ResolveOffset("timetable_hour", offsets.TimetableHour, moduleBytes),
            CurrentDistance = ResolveOffset("current_distance", offsets.CurrentDistance, moduleBytes),
            TargetStopDistance = ResolveOffset("target_stop_distance", offsets.TargetStopDistance, moduleBytes),
            LinePath = ResolveOffset("line_path", offsets.LinePath, moduleBytes)
        };
    }

    private long ResolveOffset(string fieldName, long fallbackOffset, byte[] moduleBytes)
    {
        if (!_configuration.Signatures.TryGetValue(fieldName, out var signature))
        {
            return fallbackOffset;
        }

        var address = ResolveSignatureAddress(fieldName, signature, moduleBytes);
        return checked(address.ToInt64() - _moduleBaseAddress.ToInt64());
    }

    private nint ResolveSignatureAddress(string fieldName, MemoryAddressSignature signature, byte[] moduleBytes)
    {
        var pattern = ParseBytePattern(signature.Pattern, fieldName);
        var matchIndices = FindPatternMatches(moduleBytes, pattern);
        if (matchIndices.Count == 0)
        {
            throw new InvalidOperationException(
                $"Signature '{fieldName}' did not match anywhere in the module image.");
        }

        var decodedAddresses = matchIndices
            .Select(matchIndex => DecodeSignatureAddress(fieldName, signature, moduleBytes, matchIndex))
            .Distinct()
            .ToArray();
        if (decodedAddresses.Length != 1)
        {
            throw new InvalidOperationException(
                $"Signature '{fieldName}' matched {matchIndices.Count} locations that resolve to {decodedAddresses.Length} different addresses.");
        }

        var address = (nint)checked(decodedAddresses[0] + signature.AddressOffset);
        foreach (var offset in signature.PointerOffsets)
        {
            var pointerBytes = ReadBytesAt(address, signature.PointerSize);
            var pointer = signature.PointerSize == sizeof(uint)
                ? BitConverter.ToUInt32(pointerBytes, 0)
                : BitConverter.ToInt64(pointerBytes, 0);
            if (pointer == 0)
            {
                throw new InvalidOperationException($"Signature '{fieldName}' resolved a null pointer.");
            }

            address = (nint)checked(pointer + offset);
        }

        return address;
    }

    private long DecodeSignatureAddress(
        string fieldName,
        MemoryAddressSignature signature,
        byte[] moduleBytes,
        int matchIndex)
    {
        return signature.AddressMode switch
        {
            MemorySignatureAddressMode.Match => checked(_moduleBaseAddress.ToInt64() + matchIndex + signature.MatchOffset),
            MemorySignatureAddressMode.Absolute32 => ReadAbsolute32(fieldName, signature, moduleBytes, matchIndex),
            MemorySignatureAddressMode.Absolute64 => ReadAbsolute64(fieldName, signature, moduleBytes, matchIndex),
            MemorySignatureAddressMode.Relative32 => ReadRelative32(fieldName, signature, moduleBytes, matchIndex),
            _ => throw new InvalidOperationException($"Unsupported address mode for signature '{fieldName}'.")
        };
    }

    private static long ReadAbsolute32(string fieldName, MemoryAddressSignature signature, byte[] moduleBytes, int matchIndex)
    {
        EnsureOperandFits(moduleBytes, matchIndex, signature.OperandOffset, sizeof(uint), fieldName);
        return BitConverter.ToUInt32(moduleBytes, matchIndex + signature.OperandOffset);
    }

    private static long ReadAbsolute64(string fieldName, MemoryAddressSignature signature, byte[] moduleBytes, int matchIndex)
    {
        EnsureOperandFits(moduleBytes, matchIndex, signature.OperandOffset, sizeof(long), fieldName);
        return BitConverter.ToInt64(moduleBytes, matchIndex + signature.OperandOffset);
    }

    private long ReadRelative32(string fieldName, MemoryAddressSignature signature, byte[] moduleBytes, int matchIndex)
    {
        EnsureOperandFits(moduleBytes, matchIndex, signature.OperandOffset, sizeof(int), fieldName);
        var displacement = BitConverter.ToInt32(moduleBytes, matchIndex + signature.OperandOffset);
        return checked(_moduleBaseAddress.ToInt64() + matchIndex + signature.OperandOffset + sizeof(int) + displacement);
    }

    private static byte?[] ParseBytePattern(string pattern, string fieldName)
    {
        var tokens = pattern.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            throw new InvalidOperationException($"Signature '{fieldName}' has an empty pattern.");
        }

        return tokens.Select(token => token is "?" or "??"
            ? (byte?)null
            : byte.TryParse(token, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new InvalidOperationException($"Signature '{fieldName}' has invalid byte '{token}'."))
            .ToArray();
    }

    private static List<int> FindPatternMatches(byte[] source, byte?[] pattern)
    {
        var matches = new List<int>();
        for (var start = 0; start <= source.Length - pattern.Length; start++)
        {
            var isMatch = true;
            for (var index = 0; index < pattern.Length; index++)
            {
                if (pattern[index] is byte expected && source[start + index] != expected)
                {
                    isMatch = false;
                    break;
                }
            }

            if (isMatch)
            {
                matches.Add(start);
            }
        }

        return matches;
    }

    private static void EnsureOperandFits(byte[] moduleBytes, int matchIndex, int operandOffset, int operandSize, string fieldName)
    {
        if (operandOffset < 0 || matchIndex + operandOffset > moduleBytes.Length - operandSize)
        {
            throw new InvalidOperationException($"Signature '{fieldName}' operand falls outside the module image.");
        }
    }

    private string? ReadLinePath(long relativeOffset)
    {
        if (relativeOffset <= 0)
        {
            return null;
        }

        var bytes = ReadBytes(relativeOffset, LinePathReadLength);
        var terminatorIndex = Array.IndexOf(bytes, (byte)0);
        var contentLength = terminatorIndex >= 0 ? terminatorIndex : bytes.Length;
        if (contentLength <= 0)
        {
            return null;
        }

        var value = Encoding.UTF8.GetString(bytes, 0, contentLength).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int ReadInt32(byte[][] segmentBuffers, FieldReadInfo field)
    {
        return BitConverter.ToInt32(segmentBuffers[field.SegmentIndex], field.BufferOffset);
    }

    private static byte ReadByte(byte[][] segmentBuffers, FieldReadInfo field)
    {
        return segmentBuffers[field.SegmentIndex][field.BufferOffset];
    }

    private static double ReadDouble(byte[][] segmentBuffers, FieldReadInfo field)
    {
        return BitConverter.ToDouble(segmentBuffers[field.SegmentIndex], field.BufferOffset);
    }

    private static SnapshotReadPlan BuildSnapshotReadPlan(MemoryOffsets offsets)
    {
        var fields = new List<FieldDefinition>
        {
            new("next_station_id", offsets.NextStationId, 4),
            new("door_state", offsets.DoorState, 1),
            new("current_time_seconds", offsets.CurrentTimeSeconds, 4),
            new("current_time_minutes", offsets.CurrentTimeMinutes, 4),
            new("current_time_hours", offsets.CurrentTimeHours, 4),
            new("timetable_second", offsets.TimetableSecond, 4),
            new("timetable_minute", offsets.TimetableMinute, 4),
            new("timetable_hour", offsets.TimetableHour, 4),
            new("current_distance", offsets.CurrentDistance, 8),
            new("target_stop_distance", offsets.TargetStopDistance, 8)
        };

        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var segments = new List<ReadSegment>();
        var fieldInfos = new Dictionary<string, FieldReadInfo>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            if (segments.Count == 0)
            {
                var firstSegment = new ReadSegment(field.Offset, field.Size);
                segments.Add(firstSegment);
                fieldInfos[field.Name] = new FieldReadInfo(0, 0);
                continue;
            }

            var lastIndex = segments.Count - 1;
            var lastSegment = segments[lastIndex];
            var lastEndExclusive = lastSegment.StartOffset + lastSegment.Size;
            var fieldEndExclusive = field.Offset + field.Size;

            if (field.Offset <= lastEndExclusive + MergeAdjacentGapBytes)
            {
                var newEndExclusive = Math.Max(lastEndExclusive, fieldEndExclusive);
                lastSegment.Size = (int)(newEndExclusive - lastSegment.StartOffset);
                fieldInfos[field.Name] = new FieldReadInfo(lastIndex, (int)(field.Offset - lastSegment.StartOffset));
                continue;
            }

            var nextSegment = new ReadSegment(field.Offset, field.Size);
            segments.Add(nextSegment);
            fieldInfos[field.Name] = new FieldReadInfo(segments.Count - 1, 0);
        }

        return new SnapshotReadPlan(
            segments,
            fieldInfos["next_station_id"],
            fieldInfos["door_state"],
            fieldInfos["current_time_seconds"],
            fieldInfos["current_time_minutes"],
            fieldInfos["current_time_hours"],
            fieldInfos["timetable_second"],
            fieldInfos["timetable_minute"],
            fieldInfos["timetable_hour"],
            fieldInfos["current_distance"],
            fieldInfos["target_stop_distance"]);
    }

    private static SnapshotReadPlan BuildSnapshotReadPlanWithoutStationId(MemoryOffsets offsets)
    {
        var fields = new List<FieldDefinition>
        {
            new("door_state", offsets.DoorState, 1),
            new("current_time_seconds", offsets.CurrentTimeSeconds, 4),
            new("current_time_minutes", offsets.CurrentTimeMinutes, 4),
            new("current_time_hours", offsets.CurrentTimeHours, 4),
            new("timetable_second", offsets.TimetableSecond, 4),
            new("timetable_minute", offsets.TimetableMinute, 4),
            new("timetable_hour", offsets.TimetableHour, 4),
            new("current_distance", offsets.CurrentDistance, 8),
            new("target_stop_distance", offsets.TargetStopDistance, 8)
        };

        fields.Sort((a, b) => a.Offset.CompareTo(b.Offset));

        var segments = new List<ReadSegment>();
        var fieldInfos = new Dictionary<string, FieldReadInfo>(StringComparer.Ordinal);

        foreach (var field in fields)
        {
            if (segments.Count == 0)
            {
                var firstSegment = new ReadSegment(field.Offset, field.Size);
                segments.Add(firstSegment);
                fieldInfos[field.Name] = new FieldReadInfo(0, 0);
                continue;
            }

            var lastIndex = segments.Count - 1;
            var lastSegment = segments[lastIndex];
            var lastEndExclusive = lastSegment.StartOffset + lastSegment.Size;
            var fieldEndExclusive = field.Offset + field.Size;

            if (field.Offset <= lastEndExclusive + MergeAdjacentGapBytes)
            {
                var newEndExclusive = Math.Max(lastEndExclusive, fieldEndExclusive);
                lastSegment.Size = (int)(newEndExclusive - lastSegment.StartOffset);
                fieldInfos[field.Name] = new FieldReadInfo(lastIndex, (int)(field.Offset - lastSegment.StartOffset));
                continue;
            }

            var nextSegment = new ReadSegment(field.Offset, field.Size);
            segments.Add(nextSegment);
            fieldInfos[field.Name] = new FieldReadInfo(segments.Count - 1, 0);
        }

        return new SnapshotReadPlan(
            segments,
            null,
            fieldInfos["door_state"],
            fieldInfos["current_time_seconds"],
            fieldInfos["current_time_minutes"],
            fieldInfos["current_time_hours"],
            fieldInfos["timetable_second"],
            fieldInfos["timetable_minute"],
            fieldInfos["timetable_hour"],
            fieldInfos["current_distance"],
            fieldInfos["target_stop_distance"]);
    }

    private byte[] ReadBytes(long relativeOffset, int byteCount)
    {
        return ReadBytesAt(_moduleBaseAddress + (nint)relativeOffset, byteCount, relativeOffset);
    }

    private byte[] ReadBytesAt(nint absoluteAddress, int byteCount, long? relativeOffsetForError = null)
    {
        if (_processHandle == nint.Zero)
        {
            throw new InvalidOperationException("Process is not attached.");
        }

        var buffer = new byte[byteCount];

        if (!ReadProcessMemory(_processHandle, absoluteAddress, buffer, byteCount, out var bytesRead) || bytesRead != byteCount)
        {
            var location = relativeOffsetForError is long relativeOffset
                ? $"offset 0x{relativeOffset:X}"
                : $"address 0x{absoluteAddress.ToInt64():X}";
            throw new InvalidOperationException($"ReadProcessMemory failed at {location}.");
        }

        return buffer;
    }

    private readonly record struct SnapshotValues
    {
        public required int NextStationId { get; init; }

        public required byte DoorState { get; init; }

        public required int CurrentTimeSeconds { get; init; }

        public required int CurrentTimeMinutes { get; init; }

        public required int CurrentTimeHours { get; init; }

        public required int TimetableSecond { get; init; }

        public required int TimetableMinute { get; init; }

        public required int TimetableHour { get; init; }

        public required double CurrentDistanceMeters { get; init; }

        public required double TargetStopDistanceMeters { get; init; }

        public string? LinePath { get; init; }
    }

    private sealed record FieldDefinition(string Name, long Offset, int Size);

    private sealed class ReadSegment
    {
        public ReadSegment(long startOffset, int size)
        {
            StartOffset = startOffset;
            Size = size;
        }

        public long StartOffset { get; }

        public int Size { get; set; }
    }

    private readonly record struct FieldReadInfo(int SegmentIndex, int BufferOffset);

    private sealed record SnapshotReadPlan(
        IReadOnlyList<ReadSegment> Segments,
        FieldReadInfo? NextStationIdField,
        FieldReadInfo DoorStateField,
        FieldReadInfo CurrentTimeSecondsField,
        FieldReadInfo CurrentTimeMinutesField,
        FieldReadInfo CurrentTimeHoursField,
        FieldReadInfo TimetableSecondField,
        FieldReadInfo TimetableMinuteField,
        FieldReadInfo TimetableHourField,
        FieldReadInfo CurrentDistanceField,
        FieldReadInfo TargetStopDistanceField);

    private void Release()
    {
        if (_processHandle != nint.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = nint.Zero;
        }

        _process = null;
        _moduleBaseAddress = nint.Zero;
        _moduleMemorySize = 0;
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        nint hProcess,
        nint lpBaseAddress,
        [Out] byte[] lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint hObject);
}
