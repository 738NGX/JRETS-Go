using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class DebugRealtimeDataSource : IRealtimeDataSource
{
    private enum DebugPhase
    {
        /// <summary>停靠站，开门: ただいま</summary>
        Stopped,
        /// <summary>发车，关门: 次は</summary>
        Departed,
        /// <summary>接近下一站: まもなく</summary>
        Approaching
    }

    private readonly IReadOnlyList<StationInfo> _stations;
    private int _currentStationIndex;
    private DebugPhase _currentPhase = DebugPhase.Stopped;

    private bool _doorOpen = true;
    private int _clockSeconds = 8 * 3600;
    private double _currentDistance;
    private double _targetStopDistance = 500;

    public DebugRealtimeDataSource(IReadOnlyList<StationInfo> stations)
    {
        if (stations.Count < 2)
        {
            throw new ArgumentException("Debug mode requires at least two stations.", nameof(stations));
        }

        _stations = stations.OrderBy(x => x.Id).ToArray();
        _currentStationIndex = 0;
    }

    public RealtimeSnapshot GetSnapshot()
    {
        var current = _stations[_currentStationIndex];
        var timetable = TimeSpan.FromSeconds(_clockSeconds + 120);

        return new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = current.Id,
            DoorOpen = _doorOpen,
            MainClockSeconds = _clockSeconds,
            TimetableHour = timetable.Hours,
            TimetableMinute = timetable.Minutes,
            TimetableSecond = timetable.Seconds,
            CurrentDistanceMeters = _currentDistance,
            TargetStopDistanceMeters = _targetStopDistance
        };
    }

    public void StartSession()
    {
        _doorOpen = true;
        _clockSeconds = 8 * 3600;
        _currentDistance = 0;
        _targetStopDistance = 500;
        _currentStationIndex = 0;
        _currentPhase = DebugPhase.Stopped;
    }

    /// <summary>
    /// Advance to next phase: ただいま → 次は → まもなく → ただいま(Next Station)
    /// </summary>
    public void DebugAdvance()
    {
        switch (_currentPhase)
        {
            case DebugPhase.Stopped:
                // ただいま → 次は (close door and depart)
                _currentPhase = DebugPhase.Departed;
                _doorOpen = false;
                break;

            case DebugPhase.Departed:
                // 次は → まもなく (train approaches next station, distance < 350m)
                _currentPhase = DebugPhase.Approaching;
                _currentDistance = _targetStopDistance - 300; // approaching: < 350m away
                break;

            case DebugPhase.Approaching:
                // まもなく → ただいま(Next Station) (arrive and open door)
                if (_currentStationIndex < _stations.Count - 1)
                {
                    _currentStationIndex++;
                }

                _currentPhase = DebugPhase.Stopped;
                _doorOpen = true;
                _currentDistance = _targetStopDistance;
                _targetStopDistance += 900;
                _clockSeconds += 180;
                break;
        }
    }

    public void DebugDepart()
    {
        _doorOpen = false;
    }

    public void DebugArrive()
    {
        if (_currentStationIndex < _stations.Count - 1)
        {
            _currentStationIndex++;
        }

        _doorOpen = true;
        _currentDistance = _targetStopDistance;
        _targetStopDistance += 900;
        _clockSeconds += 180;
    }

    public void TickRunning()
    {
        if (_doorOpen)
        {
            return;
        }

        // In debug mode, distance and time should only change via explicit DebugAdvance() calls
        // Not automatically in TickRunning()
    }
}
