using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JRETS.Go.Core.Runtime;
using JRETS.Go.Core.Services;

namespace JRETS.Go.App;

public partial class MainWindow
{
    private bool TryActivateLiveMemoryMode()
    {
        if (_memoryDataSource is null)
        {
            return false;
        }

        if (_memoryDataSource.TryAttach())
        {
            _lastDataSourceError = string.Empty;
            return true;
        }

        _lastDataSourceError = _memoryDataSource.LastAttachError;
        return false;
    }

    private RealtimeSnapshot GetCurrentSnapshot()
    {
        if (_sessionRunning && _usingLiveMemory && _memoryDataSource is not null)
        {
            if (TryGetLatestLiveMemorySnapshot(out var cachedSnapshot))
            {
                return cachedSnapshot;
            }

            try
            {
                _lastDataSourceError = string.Empty;
                var snapshot = ReadLiveMemorySnapshotWithStationPolicy();
                SetLatestLiveMemorySnapshot(snapshot);
                return snapshot;
            }
            catch (Exception ex)
            {
                _lastDataSourceError = ex.Message;
                _usingLiveMemory = false;
                StopLiveMemorySampling();
                _debugDataSource.StartSession();
            }
        }

        return _debugDataSource.GetSnapshot();
    }

    private void StartLiveMemorySampling()
    {
        StopLiveMemorySampling();

        if (_memoryDataSource is null)
        {
            return;
        }

        lock (_liveSnapshotSync)
        {
            _latestLiveMemorySnapshot = null;
            _latestLiveMemorySnapshotAtUtc = DateTime.MinValue;
            _liveMemorySamplingLastError = null;
        }

        _liveMemorySamplingCancellation = new CancellationTokenSource();
        var token = _liveMemorySamplingCancellation.Token;

        _liveMemorySamplingTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                var delayMs = LiveMemorySamplingCruiseIntervalMs;

                try
                {
                    var snapshot = ReadLiveMemorySnapshotWithStationPolicy();
                    SetLatestLiveMemorySnapshot(snapshot);
                    delayMs = _sessionRunning ? GetLiveMemorySamplingDelayMs(snapshot) : LiveMemorySamplingCruiseIntervalMs;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lock (_liveSnapshotSync)
                    {
                        _liveMemorySamplingLastError = ex.Message;
                    }

                    delayMs = LiveMemorySamplingErrorBackoffIntervalMs;
                }

                try
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    private int GetLiveMemorySamplingDelayMs(RealtimeSnapshot snapshot)
    {
        if (snapshot.DoorOpen)
        {
            return LiveMemorySamplingCruiseIntervalMs;
        }

        // Only enter high-frequency sampling when approach data has been latched at door-close transition.
        // Never use dynamic snapshot data for this decision.
        if (_activeApproachTargetStopDistance is null)
        {
            return LiveMemorySamplingCruiseIntervalMs;
        }

        var remainingMeters = _activeApproachTargetStopDistance.Value - snapshot.CurrentDistanceMeters;
        return remainingMeters <= LiveMemoryApproachSamplingTriggerMeters
            ? LiveMemorySamplingApproachIntervalMs
            : LiveMemorySamplingCruiseIntervalMs;
    }

    private void StopLiveMemorySampling()
    {
        var cancellation = _liveMemorySamplingCancellation;
        _liveMemorySamplingCancellation = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _liveMemorySamplingTask = null;

        lock (_liveSnapshotSync)
        {
            _latestLiveMemorySnapshot = null;
            _latestLiveMemorySnapshotAtUtc = DateTime.MinValue;
            _liveMemorySamplingLastError = null;
            _liveStationAnchorId = null;
            _lastLiveDoorOpen = null;
        }
    }

    private void SetLatestLiveMemorySnapshot(RealtimeSnapshot snapshot)
    {
        lock (_liveSnapshotSync)
        {
            _latestLiveMemorySnapshot = snapshot;
            _latestLiveMemorySnapshotAtUtc = DateTime.UtcNow;
            _liveMemorySamplingLastError = null;
        }
    }

    private bool TryGetLatestLiveMemorySnapshot(out RealtimeSnapshot snapshot)
    {
        lock (_liveSnapshotSync)
        {
            if (_latestLiveMemorySnapshot is not null
                && _latestLiveMemorySnapshotAtUtc != DateTime.MinValue
                && (DateTime.UtcNow - _latestLiveMemorySnapshotAtUtc).TotalMilliseconds <= LiveMemorySnapshotStaleMs)
            {
                snapshot = _latestLiveMemorySnapshot;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(_liveMemorySamplingLastError))
            {
                _lastDataSourceError = _liveMemorySamplingLastError;
            }
        }

        snapshot = null!;
        return false;
    }

    private void ReloadConfigurations()
    {
        LoadLineConfigurationOptions();
        LoadLinePathMappings();
        LoadUpdateConfiguration();

        _stopScoringService = new StopScoringService();

        StopLiveMemorySampling();
        _memoryDataSource?.Dispose();
        _memoryDataSource = null;

        _memoryDataSource = _appConfigurationLoader.CreateMemoryDataSource(_offsetsConfigPath);
    }

    private void LoadUpdateConfiguration()
    {
        try
        {
            if (!File.Exists(_updateConfigPath))
            {
                _updateConfiguration = null;
                return;
            }

            _updateConfiguration = _appConfigurationLoader.LoadUpdateConfiguration(_updateConfigPath);
        }
        catch (Exception ex)
        {
            _updateConfiguration = null;
            _lastDataSourceError = $"Update config load failed: {ex.Message}";
            WriteUpdateConfigLoadFailureLog(_updateConfigPath, ex);
        }
    }

    private static void WriteUpdateConfigLoadFailureLog(string configPath, Exception ex)
    {
        try
        {
            var logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "JRETS.Go.App",
                "logs");
            Directory.CreateDirectory(logDirectory);

            var logPath = Path.Combine(logDirectory, "update-config.log");
            var message = string.Join(Environment.NewLine,
                "==================================================",
                $"[{DateTimeOffset.Now:O}] Update config load failed",
                $"ConfigPath: {configPath}",
                $"Message: {ex.Message}",
                "Exception:",
                ex.ToString(),
                string.Empty);

            File.AppendAllText(logPath, message);
        }
        catch
        {
            // Keep startup resilient even if diagnostics cannot be written.
        }
    }

    private void LoadLinePathMappings()
    {
        _linePathMappingByPath.Clear();

        var mappings = _appConfigurationLoader.LoadLinePathMappings(_linePathMappingsConfigPath, NormalizeLinePath);
        foreach (var (key, entry) in mappings)
        {
            _linePathMappingByPath[key] = entry;
        }
    }

    private void LoadLineConfigurationOptions()
    {
        var lineConfigsDirectory = Path.Combine(AppContext.BaseDirectory, "configs", "lines");
        var previousPath = _lineConfigPath;
        var loadedOptions = _appConfigurationLoader.LoadLineConfigurations(lineConfigsDirectory, previousPath);

        _lineConfigOptions.Clear();

        foreach (var option in loadedOptions)
        {
            _lineConfigOptions.Add(new LineConfigurationOption
            {
                FilePath = option.FilePath,
                Configuration = option.Configuration,
                DisplayName = option.DisplayName
            });
        }

        var selected = _lineConfigOptions.FirstOrDefault(x =>
            string.Equals(x.FilePath, previousPath, StringComparison.OrdinalIgnoreCase)) ?? _lineConfigOptions.FirstOrDefault();

        _suppressComboChange = true;
        LineConfigComboBox.ItemsSource = null;
        LineConfigComboBox.ItemsSource = _lineConfigOptions;
        LineConfigComboBox.SelectedItem = selected;
        _suppressComboChange = false;

        if (selected is not null)
        {
            ApplyLineConfiguration(selected);
        }
    }

    private RealtimeSnapshot ReadLiveMemorySnapshotWithStationPolicy()
    {
        if (_memoryDataSource is null)
        {
            throw new InvalidOperationException("Memory data source is not initialized.");
        }

        lock (_liveSnapshotSync)
        {
            if (!_liveStationAnchorId.HasValue)
            {
                var initial = _memoryDataSource.GetSnapshot();
                var initialAnchor = IsConfiguredStationId(initial.NextStationId)
                    ? initial.NextStationId
                    : ResolveFallbackAnchorStationId();

                _liveStationAnchorId = initialAnchor;
                _lastLiveDoorOpen = initial.DoorOpen;

                if (initial.NextStationId == initialAnchor)
                {
                    return initial;
                }

                return CloneSnapshotWithStationId(initial, initialAnchor);
            }

            var anchorStationId = _liveStationAnchorId.Value;
            var lightweight = _memoryDataSource.GetSnapshotWithoutStationId(anchorStationId);
            var previousDoorOpen = _lastLiveDoorOpen ?? lightweight.DoorOpen;

            if (previousDoorOpen && !lightweight.DoorOpen)
            {
                // Refresh station anchor exactly once at departure edge.
                var refreshed = _memoryDataSource.GetSnapshot();
                if (IsConfiguredStationId(refreshed.NextStationId))
                {
                    _liveStationAnchorId = refreshed.NextStationId;
                    _lastLiveDoorOpen = refreshed.DoorOpen;
                    return refreshed;
                }

                _lastLiveDoorOpen = refreshed.DoorOpen;
                return CloneSnapshotWithStationId(refreshed, _liveStationAnchorId.Value);
            }

            if (!previousDoorOpen && lightweight.DoorOpen)
            {
                var advancedAnchor = ResolveNextAnchorStationId(anchorStationId);
                _liveStationAnchorId = advancedAnchor;
                _lastLiveDoorOpen = true;
                return CloneSnapshotWithStationId(lightweight, advancedAnchor);
            }

            _lastLiveDoorOpen = lightweight.DoorOpen;
            return lightweight;
        }
    }

    private bool IsConfiguredStationId(int stationId)
    {
        return _lineConfiguration.Stations.Any(x => x.Id == stationId);
    }

    private int ResolveFallbackAnchorStationId()
    {
        if (_lineConfiguration.Stations.Count > 0)
        {
            return _lineConfiguration.Stations[0].Id;
        }

        return 0;
    }

    private int ResolveNextAnchorStationId(int currentAnchorStationId)
    {
        var stations = _lineConfiguration.Stations;
        if (stations.Count == 0)
        {
            return currentAnchorStationId;
        }

        var currentIndex = -1;
        for (var i = 0; i < stations.Count; i++)
        {
            if (stations[i].Id == currentAnchorStationId)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            return ResolveFallbackAnchorStationId();
        }

        if (currentIndex < stations.Count - 1)
        {
            return stations[currentIndex + 1].Id;
        }

        return _lineConfiguration.LineInfo.IsLoop ? stations[0].Id : stations[currentIndex].Id;
    }

    private static RealtimeSnapshot CloneSnapshotWithStationId(RealtimeSnapshot source, int stationId)
    {
        return new RealtimeSnapshot
        {
            CapturedAt = source.CapturedAt,
            NextStationId = stationId,
            DoorOpen = source.DoorOpen,
            MainClockSeconds = source.MainClockSeconds,
            TimetableHour = source.TimetableHour,
            TimetableMinute = source.TimetableMinute,
            TimetableSecond = source.TimetableSecond,
            CurrentDistanceMeters = source.CurrentDistanceMeters,
            TargetStopDistanceMeters = source.TargetStopDistanceMeters,
            LinePath = source.LinePath
        };
    }

    private void ApplyLineConfiguration(LineConfigurationOption option)
    {
        _lineConfigPath = option.FilePath;
        _lineConfiguration = option.Configuration;
        _hudStatusMessage = null;
        _debugDataSource = new DebugRealtimeDataSource(
            _lineConfiguration.Stations,
            TryLoadDebugStationDisplacementsMeters(_lineConfiguration));
        _timelineInitialized = false;
        PopulateServiceOptions();

        lock (_liveSnapshotSync)
        {
            _liveStationAnchorId = null;
            _lastLiveDoorOpen = null;
        }

        if (_sessionRunning && !_usingLiveMemory)
        {
            _debugDataSource.StartSession();
        }
    }

}
