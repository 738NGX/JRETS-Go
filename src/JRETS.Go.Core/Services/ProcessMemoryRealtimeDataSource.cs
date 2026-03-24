using System.Diagnostics;
using System.Runtime.InteropServices;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class ProcessMemoryRealtimeDataSource : IRealtimeDataSource, IDisposable
{
    private const int ProcessVmRead = 0x0010;
    private const int ProcessQueryInformation = 0x0400;

    private readonly MemoryOffsetsConfiguration _configuration;

    private Process? _process;
    private nint _processHandle;
    private nint _moduleBaseAddress;

    public string LastAttachError { get; private set; } = string.Empty;

    public ProcessMemoryRealtimeDataSource(MemoryOffsetsConfiguration configuration)
    {
        _configuration = configuration;
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

        var o = _configuration.Offsets;

        var nextStationId = ReadInt32(o.NextStationId);
        var doorState = ReadInt32(o.DoorState);
        var mainClock = ReadInt32(o.MainClockSeconds);
        var timetableSec = ReadInt32(o.TimetableSecond);
        var timetableMin = ReadInt32(o.TimetableMinute);
        var timetableHour = ReadInt32(o.TimetableHour);
        var currentDistance = ReadDouble(o.CurrentDistance);
        var targetStopDistance = ReadDouble(o.TargetStopDistance);

        return new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = nextStationId,
            DoorOpen = doorState == 1,
            MainClockSeconds = mainClock,
            TimetableHour = timetableHour,
            TimetableMinute = timetableMin,
            TimetableSecond = timetableSec,
            CurrentDistanceMeters = currentDistance,
            TargetStopDistanceMeters = targetStopDistance
        };
    }

    private int ReadInt32(long relativeOffset)
    {
        var bytes = ReadBytes(relativeOffset, 4);
        return BitConverter.ToInt32(bytes, 0);
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
