using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class ProcessMemoryRealtimeDataSource : IRealtimeDataSource, IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;
    private const int MergeAdjacentGapBytes = 64;
    private const int LinePathReadLength = 1024;

    private readonly MemoryOffsetsConfiguration _configuration;
    private readonly SnapshotReadPlan _snapshotReadPlan;

    private Process? _process;
    private nint _processHandle;
    private nint _moduleBaseAddress;

    public string LastAttachError { get; private set; } = string.Empty;

    public ProcessMemoryRealtimeDataSource(MemoryOffsetsConfiguration configuration)
    {
        _configuration = configuration;
        _snapshotReadPlan = BuildSnapshotReadPlan(configuration.Offsets);
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
        LastAttachError = string.Empty;
        return true;
    }

    public RealtimeSnapshot GetSnapshot()
    {
        if ((_process is null || _process.HasExited || _processHandle == nint.Zero) && !TryAttach())
        {
            throw new InvalidOperationException(LastAttachError);
        }

        var values = ReadSnapshotValues();

        return new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = values.NextStationId,
            // Game door state is non-binary on some lines (e.g. transient 44 when opening).
            // Treat any non-zero value as "door open" at snapshot level.
            DoorOpen = values.DoorState != 0,
            MainClockSeconds = values.MainClockSeconds,
            TimetableHour = values.TimetableHour,
            TimetableMinute = values.TimetableMinute,
            TimetableSecond = values.TimetableSecond,
            CurrentDistanceMeters = values.CurrentDistanceMeters,
            TargetStopDistanceMeters = values.TargetStopDistanceMeters,
            LinePath = values.LinePath
        };
    }

    private SnapshotValues ReadSnapshotValues()
    {
        if (_processHandle == nint.Zero)
        {
            throw new InvalidOperationException("Process is not attached.");
        }

        var segmentBuffers = new byte[_snapshotReadPlan.Segments.Count][];
        for (var i = 0; i < _snapshotReadPlan.Segments.Count; i++)
        {
            var segment = _snapshotReadPlan.Segments[i];
            segmentBuffers[i] = ReadBytes(segment.StartOffset, segment.Size);
        }

        return new SnapshotValues
        {
            NextStationId = ReadInt32(segmentBuffers, _snapshotReadPlan.NextStationIdField),
            DoorState = ReadByte(segmentBuffers, _snapshotReadPlan.DoorStateField),
            MainClockSeconds = ReadInt32(segmentBuffers, _snapshotReadPlan.MainClockSecondsField),
            TimetableSecond = ReadInt32(segmentBuffers, _snapshotReadPlan.TimetableSecondField),
            TimetableMinute = ReadInt32(segmentBuffers, _snapshotReadPlan.TimetableMinuteField),
            TimetableHour = ReadInt32(segmentBuffers, _snapshotReadPlan.TimetableHourField),
            CurrentDistanceMeters = ReadDouble(segmentBuffers, _snapshotReadPlan.CurrentDistanceField),
            TargetStopDistanceMeters = ReadDouble(segmentBuffers, _snapshotReadPlan.TargetStopDistanceField),
            LinePath = ReadLinePath(_configuration.Offsets.LinePath)
        };
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
            new("main_clock_seconds", offsets.MainClockSeconds, 4),
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
            fieldInfos["main_clock_seconds"],
            fieldInfos["timetable_second"],
            fieldInfos["timetable_minute"],
            fieldInfos["timetable_hour"],
            fieldInfos["current_distance"],
            fieldInfos["target_stop_distance"]);
    }

    private int ReadInt32(long relativeOffset)
    {
        var bytes = ReadBytes(relativeOffset, 4);
        return BitConverter.ToInt32(bytes, 0);
    }

    private byte ReadByte(long relativeOffset)
    {
        var bytes = ReadBytes(relativeOffset, 1);
        return bytes[0];
    }

    private double ReadDouble(long relativeOffset)
    {
        var bytes = ReadBytes(relativeOffset, 8);
        return BitConverter.ToDouble(bytes, 0);
    }

    private byte[] ReadBytes(long relativeOffset, int byteCount)
    {
        if (_processHandle == nint.Zero)
        {
            throw new InvalidOperationException("Process is not attached.");
        }

        var absoluteAddress = _moduleBaseAddress + (nint)relativeOffset;
        var buffer = new byte[byteCount];

        if (!ReadProcessMemory(_processHandle, absoluteAddress, buffer, byteCount, out var bytesRead) || bytesRead != byteCount)
        {
            throw new InvalidOperationException($"ReadProcessMemory failed at offset 0x{relativeOffset:X}.");
        }

        return buffer;
    }

    private readonly record struct SnapshotValues
    {
        public required int NextStationId { get; init; }

        public required byte DoorState { get; init; }

        public required int MainClockSeconds { get; init; }

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
        FieldReadInfo NextStationIdField,
        FieldReadInfo DoorStateField,
        FieldReadInfo MainClockSecondsField,
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
