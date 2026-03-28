using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class DebugRealtimeDataSource : IRealtimeDataSource
{
    private const double FallbackStationSpacingMeters = 1000;
    private const double DebugDepartureAdvanceMeters = 200;
    private const double DebugApproachRemainingMeters = 100;
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
    private readonly double[] _stationStopDisplacements;
    private int _currentStationIndex;
    private DebugPhase _currentPhase = DebugPhase.Stopped;

    private bool _doorOpen = true;
    private int _clockSeconds = 8 * 3600;
    private double _currentDistance;
    private double _targetStopDistance;

    public DebugRealtimeDataSource(
        IReadOnlyList<StationInfo> stations,
        IReadOnlyDictionary<int, double>? stationDisplacementsMeters = null)
    {
        if (stations.Count < 2)
        {
            throw new ArgumentException("Debug mode requires at least two stations.", nameof(stations));
        }

        _stations = stations.ToArray();
        _stationStopDisplacements = BuildStationStopDisplacements(_stations, stationDisplacementsMeters);
        _currentStationIndex = 0;
        StartSession();
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
            TargetStopDistanceMeters = _targetStopDistance,
            LinePath = null
        };
    }

    public void StartSession()
    {
        _doorOpen = true;
        _clockSeconds = 8 * 3600;
        _currentStationIndex = 0;
        _currentPhase = DebugPhase.Stopped;
        _currentDistance = ResolveCurrentStopDistance();
        _targetStopDistance = ResolveTargetStopDistance();
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
                _currentDistance = ResolveDepartedDistance();
                break;

            case DebugPhase.Departed:
                // 次は → まもなく (train approaches next station)
                _currentPhase = DebugPhase.Approaching;
                _currentDistance = ResolveApproachingDistance();
                break;

            case DebugPhase.Approaching:
                // まもなく → ただいま(Next Station) (arrive and open door)
                if (_currentStationIndex < _stations.Count - 1)
                {
                    _currentStationIndex++;
                }

                _currentPhase = DebugPhase.Stopped;
                _doorOpen = true;
                _currentDistance = ResolveCurrentStopDistance();
                _targetStopDistance = ResolveTargetStopDistance();
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
        _currentPhase = DebugPhase.Stopped;
        _currentDistance = ResolveCurrentStopDistance();
        _targetStopDistance = ResolveTargetStopDistance();
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

    private static double[] BuildStationStopDisplacements(
        IReadOnlyList<StationInfo> stations,
        IReadOnlyDictionary<int, double>? stationDisplacementsMeters)
    {
        var result = new double[stations.Count];
        var last = 0d;

        for (var i = 0; i < stations.Count; i++)
        {
            if (stationDisplacementsMeters is not null && stationDisplacementsMeters.TryGetValue(stations[i].Id, out var mapped))
            {
                last = mapped;
                result[i] = mapped;
                continue;
            }

            if (i == 0)
            {
                result[i] = 0;
                last = 0;
            }
            else
            {
                last += FallbackStationSpacingMeters;
                result[i] = last;
            }
        }

        return result;
    }

    private double ResolveCurrentStopDistance()
    {
        return _stationStopDisplacements[Math.Clamp(_currentStationIndex, 0, _stationStopDisplacements.Length - 1)];
    }

    private double ResolveTargetStopDistance()
    {
        var targetIndex = Math.Min(_currentStationIndex + 1, _stationStopDisplacements.Length - 1);
        return _stationStopDisplacements[targetIndex];
    }

    private double ResolveApproachingDistance()
    {
        var current = ResolveCurrentStopDistance();
        var target = ResolveTargetStopDistance();
        var approach = target - DebugApproachRemainingMeters;

        if (approach <= current)
        {
            approach = (current + target) / 2;
        }

        return approach;
    }

    private double ResolveDepartedDistance()
    {
        var current = ResolveCurrentStopDistance();
        var target = ResolveTargetStopDistance();
        var departed = current + DebugDepartureAdvanceMeters;
        return Math.Min(departed, target);
    }
}
