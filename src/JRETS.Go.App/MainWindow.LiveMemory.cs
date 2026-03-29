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
                var snapshot = _memoryDataSource.GetSnapshot();
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
                    var snapshot = _memoryDataSource.GetSnapshot();
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

        _stopScoringService = new StopScoringService();

        StopLiveMemorySampling();
        _memoryDataSource?.Dispose();
        _memoryDataSource = null;

        _memoryDataSource = _appConfigurationLoader.CreateMemoryDataSource(_offsetsConfigPath);
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

        if (_sessionRunning && !_usingLiveMemory)
        {
            _debugDataSource.StartSession();
        }
    }

}
