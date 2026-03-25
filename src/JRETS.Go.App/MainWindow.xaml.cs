using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using JRETS.Go.App.Interop;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;
using JRETS.Go.Core.Services;
using NAudio.Wave;

namespace JRETS.Go.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const double NextAnnouncementDepartureDistanceMeters = 200;
    private const double ApproachAnnouncementRemainingDistanceMeters = 400;
    private const double AnnouncementTargetRms = 0.12;
    private const double AnnouncementMinGain = 0.8;
    private const double AnnouncementMaxGain = 6.0;
    private const double AnnouncementLimiterThreshold = 0.98;
    private const double AnnouncementSoftClipDrive = 1.8;
    private const double MelodyPlaybackVolume = 0.7;
    private const int TimelineVisibleStationCount = 8;
    private const int TimelineVisibleTokenCount = TimelineVisibleStationCount * 2 - 1;
    private const double StationArrowWidth = 128;
    private const double SegmentArrowWidth = 32;
    private const double ArrowHeight = 32;
    private const double ArrowNotchDepth = 16;
    private const double ArrowTipWidth = 12;
    private const double OvershootFaultMeters = 5.0;
    private const int HotKeyStartSession = 1001;
    private const int HotKeyEndSession = 1002;
    private const int HotKeyToggleClickThrough = 1003;
    private const int HotKeyMelodyTogglePlayback = 1004;
    private const int HotKeyMelodyCycleSelection = 1005;

    private string _lineConfigPath;
    private readonly string _offsetsConfigPath;
    private readonly string _scoringConfigPath;
    private readonly FileSystemWatcher _configWatcher;
    private readonly DispatcherTimer _configReloadDebounceTimer;

    private LineConfiguration _lineConfiguration;
    private DebugRealtimeDataSource _debugDataSource;
    private ProcessMemoryRealtimeDataSource? _memoryDataSource;
    private readonly DisplayStateResolver _displayStateResolver;
    private StopScoringService _stopScoringService;
    private readonly DriveReportExporter _driveReportExporter;
    private readonly DriveReportReader _driveReportReader;
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<UpcomingStationItem> _upcomingStations = [];
    private readonly ObservableCollection<MelodyOptionItem> _melodyOptionItems = [];
    private readonly MediaPlayer _announcementPlayer = new();
    private readonly MediaPlayer _melodyPlaybackPlayer = new();
    private readonly ConcurrentDictionary<string, string> _announcementNormalizedPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LineConfigurationOption> _lineConfigOptions = [];
    private readonly List<TrainServiceOption> _serviceOptions = [];
    private readonly HashSet<int> _registeredHotKeys = [];

    private nint _windowHandle;
    private bool _sessionRunning;
    private bool _clickThroughEnabled;
    private bool _debugModeEnabled;
    private bool _usingLiveMemory;
    private bool _suppressComboChange;
    private bool _timelineInitialized;
    private bool _timelineLastDoorOpen;
    private bool _announcementDoorStateInitialized;
    private bool _announcementPreviousDoorOpen;
    private int _timelineActiveToken;
    private int _timelineWindowStart;
    private int? _announcementTargetStationId;
    private int? _lastNextAnnouncementStationId;
    private int? _lastApproachAnnouncementStationId;
    private string _lastDataSourceError = string.Empty;
    private double? _announcementDepartureStartDistanceMeters;
    private double _announcementPreviousDistanceMeters;
    private bool _previousDoorOpen;
    private double? _activeApproachTargetStopDistance;
    private int? _activeApproachScheduledSeconds;
    private bool _activeApproachOvershootFaultTriggered;
    private DateTime _sessionStartedAt;
    private readonly List<StationStopScore> _stationScores = [];
    private int? _lastScoredStationId;
    private TrainServiceOption? _selectedService;
    private ScoringConfiguration _scoringConfiguration = new();
    private readonly string _announcementTempDirectory = Path.Combine(Path.GetTempPath(), "JRETS.Go.App", "normalized-audio");

    private int? _melodyCurrentStationId;
    private List<string> _melodyCurrentOptions = [];
    private int _melodySelectedIndex;
    private bool _melodyIsPlaying;

    public MainWindow()
    {
        InitializeComponent();

        var configsDirectory = Path.Combine(AppContext.BaseDirectory, "configs");
        _lineConfigPath = ResolveLineConfigPath(configsDirectory);
        _offsetsConfigPath = ResolveConfigPath(configsDirectory, "memory-offsets.sample.yaml", "memory-offsets.yaml");
        _scoringConfigPath = ResolveConfigPath(configsDirectory, "scoring.sample.yaml", "scoring.yaml");

        var loader = new YamlLineConfigurationLoader();
        _lineConfiguration = loader.LoadFromFile(_lineConfigPath);

        _debugDataSource = new DebugRealtimeDataSource(_lineConfiguration.Stations);
        _displayStateResolver = new DisplayStateResolver();
        _stopScoringService = new StopScoringService(_scoringConfiguration);
        _driveReportExporter = new DriveReportExporter();
        _driveReportReader = new DriveReportReader();
        UpcomingStationsItemsControl.ItemsSource = _upcomingStations;
        MelodyOptionsItemsControl.ItemsSource = _melodyOptionItems;

        LineConfigComboBox.DisplayMemberPath = nameof(LineConfigurationOption.DisplayName);
        ServiceTypeComboBox.DisplayMemberPath = nameof(TrainServiceOption.DisplayName);

        LoadLineConfigurationOptions();

        ReloadConfigurations();

        _configWatcher = new FileSystemWatcher(Path.Combine(AppContext.BaseDirectory, "configs"), "*.yaml")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };
        _configWatcher.Changed += OnConfigChanged;
        _configWatcher.Created += OnConfigChanged;
        _configWatcher.Renamed += OnConfigChanged;
        _configWatcher.EnableRaisingEvents = true;

        _configReloadDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _configReloadDebounceTimer.Tick += ApplyDebouncedConfigReload;

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _refreshTimer.Tick += RefreshTimerOnTick;
        _refreshTimer.Start();

        _melodyPlaybackPlayer.MediaEnded += MelodyPlaybackPlayerOnMediaEnded;

        UpdateDisplay();

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void StartSessionClick(object sender, RoutedEventArgs e)
    {
        StartSession();
    }

    private void EndSessionClick(object sender, RoutedEventArgs e)
    {
        EndSession();
    }

    private void DebugAdvanceClick(object sender, RoutedEventArgs e)
    {
        DebugAdvance();
    }

    private void DebugAdvance()
    {
        if (!_sessionRunning || _usingLiveMemory)
        {
            return;
        }

        _debugDataSource.DebugAdvance();
        UpdateDisplay();
    }

    private void ToggleClickThroughClick(object sender, RoutedEventArgs e)
    {
        ToggleClickThrough();
    }

    private void ToggleDebugModeClick(object sender, RoutedEventArgs e)
    {
        _debugModeEnabled = !_debugModeEnabled;
        UpdateDisplay();
    }

    private void LineConfigSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboChange)
        {
            return;
        }

        if (LineConfigComboBox.SelectedItem is not LineConfigurationOption option)
        {
            return;
        }

        ApplyLineConfiguration(option);
        UpdateDisplay();
    }

    private void ServiceTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboChange)
        {
            return;
        }

        _selectedService = ServiceTypeComboBox.SelectedItem as TrainServiceOption;
        _timelineInitialized = false;
        UpdateDisplay();
    }

    private void ExitAppClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenLatestReportClick(object sender, RoutedEventArgs e)
    {
        OpenLatestReport();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        var interopHelper = new WindowInteropHelper(this);
        _windowHandle = interopHelper.Handle;

        var source = HwndSource.FromHwnd(_windowHandle);
        source?.AddHook(WndProc);

        RegisterGlobalHotKeys();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_windowHandle == nint.Zero)
        {
            return;
        }

        foreach (var hotKeyId in _registeredHotKeys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, hotKeyId);
        }

        _configReloadDebounceTimer.Stop();
        _configWatcher.Dispose();
        _announcementPlayer.Close();
        _melodyPlaybackPlayer.Close();
        TryCleanupAnnouncementTempDirectory();
        _memoryDataSource?.Dispose();
    }

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _configReloadDebounceTimer.Stop();
            _configReloadDebounceTimer.Start();
        });
    }

    private void ApplyDebouncedConfigReload(object? sender, EventArgs e)
    {
        _configReloadDebounceTimer.Stop();

        try
        {
            ReloadConfigurations();
            _lastDataSourceError = "Config reloaded.";
        }
        catch (Exception ex)
        {
            _lastDataSourceError = $"Config reload failed: {ex.Message}";
        }

        UpdateDisplay();
    }

    private void RegisterGlobalHotKeys()
    {
        RegisterHotKey(HotKeyStartSession, HotKeyModifiers.NoRepeat, 0x78); // F9
        RegisterHotKey(HotKeyEndSession, HotKeyModifiers.NoRepeat, 0x79); // F10
        RegisterHotKey(HotKeyToggleClickThrough, HotKeyModifiers.NoRepeat, 0x76); // F7
        RegisterHotKey(HotKeyMelodyTogglePlayback, HotKeyModifiers.NoRepeat, 0x73); // F4
        RegisterHotKey(HotKeyMelodyCycleSelection, HotKeyModifiers.Shift | HotKeyModifiers.NoRepeat, 0x09); // Shift+Tab
    }

    private void RegisterHotKey(int id, HotKeyModifiers modifiers, uint virtualKey)
    {
        if (!NativeMethods.RegisterHotKey(_windowHandle, id, (uint)modifiers, virtualKey))
        {
            _lastDataSourceError =
                $"HotKey register failed for id={id}. Some shortcuts may be unavailable because another app already uses them.";
            return;
        }

        _registeredHotKeys.Add(id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey)
        {
            return nint.Zero;
        }

        switch (wParam.ToInt32())
        {
            case HotKeyStartSession:
                StartSession();
                handled = true;
                break;
            case HotKeyEndSession:
                EndSession();
                handled = true;
                break;
            case HotKeyToggleClickThrough:
                ToggleClickThrough();
                handled = true;
                break;
            case HotKeyMelodyTogglePlayback:
                if (MelodySelectionPanel.Visibility == Visibility.Visible)
                {
                    ToggleMelodyPlayback();
                }

                handled = true;
                break;
            case HotKeyMelodyCycleSelection:
                if (MelodySelectionPanel.Visibility == Visibility.Visible)
                {
                    CycleMelodySelection(reverse: false);
                }

                handled = true;
                break;
        }

        return nint.Zero;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Melody hotkeys are handled by global RegisterHotKey (WM_HOTKEY).
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Melody hotkeys are handled by global RegisterHotKey (WM_HOTKEY).
    }

    private void OpenMelodySelectionPanel(StationInfo? station)
    {
        if (station is null)
        {
            return;
        }

        _melodyCurrentStationId = station.Id;
        _melodyCurrentOptions = station.Melody is { Count: > 0 } ? new List<string>(station.Melody) : [];
        _melodySelectedIndex = 0;
        _melodyIsPlaying = false;

        if (_melodyCurrentOptions.Count == 0)
        {
            return;
        }

        UpdateMelodyPanelDisplay();
        MelodySelectionPanel.Visibility = Visibility.Visible;
    }

    private void CloseMelodySelectionPanel()
    {
        MelodySelectionPanel.Visibility = Visibility.Collapsed;
        _melodyCurrentStationId = null;
        _melodyCurrentOptions.Clear();
        _melodySelectedIndex = 0;
        StopMelodyPlayback();
    }

    private void CycleMelodySelection(bool reverse)
    {
        if (_melodyCurrentOptions.Count == 0)
        {
            return;
        }

        if (reverse)
        {
            _melodySelectedIndex = (_melodySelectedIndex - 1 + _melodyCurrentOptions.Count) % _melodyCurrentOptions.Count;
        }
        else
        {
            _melodySelectedIndex = (_melodySelectedIndex + 1) % _melodyCurrentOptions.Count;
        }

        StopMelodyPlayback();
        UpdateMelodyPanelDisplay();
    }

    private void ToggleMelodyPlayback()
    {
        if (_melodyCurrentOptions.Count == 0)
        {
            return;
        }

        if (_melodyIsPlaying)
        {
            StopMelodyPlayback();
        }
        else
        {
            PlayMelodyFile(_melodyCurrentOptions[_melodySelectedIndex]);
        }
    }

    private void PlayMelodyFile(string melodyFilename)
    {
        var rootPath = Path.Combine(AppContext.BaseDirectory, "audio", "melodies", melodyFilename);
        var lineId = _lineConfiguration?.LineInfo.Id;
        var lineScopedPath = string.IsNullOrWhiteSpace(lineId)
            ? string.Empty
            : Path.Combine(AppContext.BaseDirectory, "audio", "melodies", lineId, melodyFilename);

        var audioPath = File.Exists(rootPath)
            ? rootPath
            : lineScopedPath;

        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return;
        }

        try
        {
            var normalizedPath = PrepareNormalizedAnnouncementAudio(audioPath);
            _melodyPlaybackPlayer.Stop();
            _melodyPlaybackPlayer.Open(new Uri(normalizedPath, UriKind.Absolute));
            _melodyPlaybackPlayer.Volume = MelodyPlaybackVolume;
            _melodyPlaybackPlayer.Position = TimeSpan.Zero;
            _melodyPlaybackPlayer.Play();
            _melodyIsPlaying = true;
            UpdateMelodyPanelDisplay();
        }
        catch
        {
            // Silently fail if playback cannot start
        }
    }

    private void StopMelodyPlayback()
    {
        _melodyPlaybackPlayer.Stop();
        _melodyIsPlaying = false;
        UpdateMelodyPanelDisplay();
    }

    private void MelodyPlaybackPlayerOnMediaEnded(object? sender, EventArgs e)
    {
        if (!_melodyIsPlaying)
        {
            return;
        }

        if (_melodyCurrentOptions.Count == 0)
        {
            _melodyIsPlaying = false;
            UpdateMelodyPanelDisplay();
            return;
        }

        try
        {
            _melodyPlaybackPlayer.Position = TimeSpan.Zero;
            _melodyPlaybackPlayer.Play();
        }
        catch
        {
            _melodyIsPlaying = false;
            UpdateMelodyPanelDisplay();
        }
    }

    private void UpdateMelodyPanelDisplay()
    {
        if (_melodyCurrentOptions.Count == 0)
        {
            MelodyCurrentText.Text = "No melody";
            _melodyOptionItems.Clear();
            return;
        }

        var selectedIndex = Math.Clamp(_melodySelectedIndex, 0, _melodyCurrentOptions.Count - 1);
        var current = _melodyCurrentOptions[selectedIndex];
        var status = _melodyIsPlaying ? "▶ Playing" : "◻ Stopped";
        MelodyCurrentText.Text = $"{status}  ({selectedIndex + 1}/{_melodyCurrentOptions.Count})";

        _melodyOptionItems.Clear();
        for (var i = 0; i < _melodyCurrentOptions.Count; i++)
        {
            var isSelected = i == selectedIndex;
            _melodyOptionItems.Add(new MelodyOptionItem
            {
                Label = $"{i + 1:D2}. {_melodyCurrentOptions[i]}",
                Background = isSelected
                    ? new SolidColorBrush(Color.FromRgb(41, 100, 151))
                    : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Foreground = isSelected
                    ? new SolidColorBrush(Color.FromRgb(234, 244, 255))
                    : new SolidColorBrush(Color.FromRgb(167, 189, 212)),
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal
            });
        }
    }

    private sealed class MelodyOptionItem
    {
        public required string Label { get; init; }

        public required Brush Background { get; init; }

        public required Brush Foreground { get; init; }

        public required FontWeight FontWeight { get; init; }
    }

    private void StartSession()
    {
        _sessionRunning = true;
        _timelineInitialized = false;
        _sessionStartedAt = DateTime.Now;
        _stationScores.Clear();
        _lastScoredStationId = null;
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
        _usingLiveMemory = TryActivateLiveMemoryMode();
        if (!_usingLiveMemory)
        {
            _debugDataSource.StartSession();
        }

        var currentSnapshot = GetCurrentSnapshot();
        _previousDoorOpen = currentSnapshot.DoorOpen;
        ResetAnnouncementState(currentSnapshot.DoorOpen);

        UpdateDisplay();
    }

    private void EndSession()
    {
        if (_sessionRunning)
        {
            ExportSessionReport();
        }

        _sessionRunning = false;
        _usingLiveMemory = false;
        _timelineInitialized = false;
        ResetAnnouncementState(doorOpen: true);
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
        CloseMelodySelectionPanel();
        UpdateDisplay();
    }

    private void ToggleClickThrough()
    {
        if (_windowHandle == nint.Zero)
        {
            return;
        }

        _clickThroughEnabled = !_clickThroughEnabled;

        var exStyle = NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GwlExStyle);
        if (_clickThroughEnabled)
        {
            exStyle |= NativeMethods.WsExTransparent | NativeMethods.WsExLayered;
        }
        else
        {
            exStyle &= ~NativeMethods.WsExTransparent;
        }

        NativeMethods.SetWindowLong(_windowHandle, NativeMethods.GwlExStyle, exStyle);
        UpdateDisplay();
    }

    private void RefreshTimerOnTick(object? sender, EventArgs e)
    {
        if (_sessionRunning && !_usingLiveMemory)
        {
            _debugDataSource.TickRunning();
        }

        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        var snapshot = GetCurrentSnapshot();
        var state = _displayStateResolver.Resolve(_lineConfiguration, snapshot);
        CaptureStopScore(snapshot, state);

        var sourceText = _usingLiveMemory ? "LiveMemory" : "Debug";

        ApplyLineColor();
        ServiceTypeTextBlock.Text = GetServiceTypeDisplayName();
        DirectionTextBlock.Text = GetDirectionText(state);

        // Determine display station and status text based on door and distance
        StationInfo? displayStation = null;
        string statusText = "次は";

        if (snapshot.DoorOpen && state.CurrentStopStation is not null)
        {
            // ただいま (currently stopped at this station)
            displayStation = state.CurrentStopStation;
            statusText = "ただいま";
        }
        else if (!snapshot.DoorOpen && state.NextStation is not null)
        {
            // Check remaining distance to next station
            var remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;
            if (remainingDistance < 400)
            {
                // まもなく (approaching next station - less than 400m away)
                displayStation = state.NextStation;
                statusText = "まもなく";
            }
            else
            {
                // 次は (normal state - more than 400m away)
                displayStation = state.NextStation;
                statusText = "次は";
            }
        }
        else
        {
            // 次は (next station, normal state)
            displayStation = state.NextStation;
            statusText = "次は";
        }

        NextStationStatusTextBlock.Text = statusText;

        var hasStationCode = displayStation is not null && !string.IsNullOrWhiteSpace(displayStation.Code);
        NextStationNameTextBlock.Text = displayStation?.NameJp ?? "--";
        StationCode.Text = hasStationCode ? displayStation!.Code! : string.Empty;
        StationCode.Foreground = hasStationCode ? Brushes.White : Brushes.Transparent;
        StationCodeBadgeOuterBorder.Background = hasStationCode ? Brushes.Black : Brushes.Transparent;
        LineCode.Text = _lineConfiguration.LineInfo.Code;
        LineNumberBadgeTextBlock.Text = displayStation is null ? "--" : displayStation.Number.ToString("00");

        RefreshUpcomingStations(snapshot, state);
        HandleAutoAnnouncements(snapshot, state);

        ErrorTextBlock.Text = string.IsNullOrWhiteSpace(_lastDataSourceError)
            ? string.Empty
            : $"Info: {_lastDataSourceError}";

        ToggleDebugModeButton.Content = _debugModeEnabled ? "Disable Debug Mode" : "Enable Debug Mode";
        DebugModeStateText.Text = _debugModeEnabled
            ? $"Mode: On ({sourceText})"
            : "Mode: Off";

        MemNextStationText.Text = $"Current Station Id: {snapshot.NextStationId}";
        MemDoorText.Text = $"Door Open: {(snapshot.DoorOpen ? "True" : "False")}";
        var clockTime = TimeSpan.FromSeconds(snapshot.MainClockSeconds);
        MemMainClockText.Text = $"Main Clock: {clockTime.Hours:D2}:{clockTime.Minutes:D2}:{clockTime.Seconds:D2}";
        MemTimetableText.Text =
            $"Timetable (H:M:S): {snapshot.TimetableHour:D2}:{snapshot.TimetableMinute:D2}:{snapshot.TimetableSecond:D2}";
        MemCurrentDistanceText.Text = $"Current Distance (m): {snapshot.CurrentDistanceMeters:F2}";
        MemTargetDistanceText.Text = $"Target Stop Distance (m): {snapshot.TargetStopDistanceMeters:F2}";

        DebugActionsPanel.Visibility = _debugModeEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CaptureStopScore(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (!_sessionRunning)
        {
            _previousDoorOpen = snapshot.DoorOpen;
            return;
        }

        var doorOpenTransition = !_previousDoorOpen && snapshot.DoorOpen;
        var doorCloseTransition = _previousDoorOpen && !snapshot.DoorOpen;

        if (doorCloseTransition)
        {
            // Latch next-stop targets at departure start and keep them until next stop scoring.
            _activeApproachTargetStopDistance = snapshot.TargetStopDistanceMeters;
            _activeApproachScheduledSeconds = snapshot.TimetableHour * 3600 + snapshot.TimetableMinute * 60 + snapshot.TimetableSecond;
            _activeApproachOvershootFaultTriggered = false;
        }

        if (!snapshot.DoorOpen && (_activeApproachTargetStopDistance is null || _activeApproachScheduledSeconds is null))
        {
            // Fallback when session starts while already running between stations.
            _activeApproachTargetStopDistance = snapshot.TargetStopDistanceMeters;
            _activeApproachScheduledSeconds = snapshot.TimetableHour * 3600 + snapshot.TimetableMinute * 60 + snapshot.TimetableSecond;
            _activeApproachOvershootFaultTriggered = false;
        }

        if (!snapshot.DoorOpen && _activeApproachTargetStopDistance is not null)
        {
            var approachPositionErrorSigned = snapshot.CurrentDistanceMeters - _activeApproachTargetStopDistance.Value;
            if (approachPositionErrorSigned >= OvershootFaultMeters)
            {
                _activeApproachOvershootFaultTriggered = true;
            }
        }

        _previousDoorOpen = snapshot.DoorOpen;

        if (!doorOpenTransition || state.CurrentStopStation is null)
        {
            return;
        }

        if (_lastScoredStationId == state.CurrentStopStation.Id)
        {
            return;
        }

        var scoringSnapshot = BuildScoringSnapshot(snapshot);
        var stopScore = _stopScoringService.ScoreStop(state.CurrentStopStation, scoringSnapshot);
        _stationScores.Add(stopScore);
        _lastScoredStationId = state.CurrentStopStation.Id;
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
    }

    private RealtimeSnapshot BuildScoringSnapshot(RealtimeSnapshot currentSnapshot)
    {
        if (_activeApproachTargetStopDistance is null || _activeApproachScheduledSeconds is null)
        {
            return currentSnapshot;
        }

        var scheduledSeconds = _activeApproachScheduledSeconds.Value;
        var scheduledHour = scheduledSeconds / 3600;
        var scheduledMinute = (scheduledSeconds % 3600) / 60;
        var scheduledSecond = scheduledSeconds % 60;
        var targetStopDistance = _activeApproachTargetStopDistance.Value;

        var adjustedCurrentDistance = currentSnapshot.CurrentDistanceMeters;
        var currentPositionErrorSigned = adjustedCurrentDistance - targetStopDistance;
        if (_activeApproachOvershootFaultTriggered && currentPositionErrorSigned < OvershootFaultMeters)
        {
            adjustedCurrentDistance = targetStopDistance + OvershootFaultMeters;
        }

        return new RealtimeSnapshot
        {
            CapturedAt = currentSnapshot.CapturedAt,
            NextStationId = currentSnapshot.NextStationId,
            DoorOpen = currentSnapshot.DoorOpen,
            MainClockSeconds = currentSnapshot.MainClockSeconds,
            TimetableHour = scheduledHour,
            TimetableMinute = scheduledMinute,
            TimetableSecond = scheduledSecond,
            CurrentDistanceMeters = adjustedCurrentDistance,
            TargetStopDistanceMeters = targetStopDistance
        };
    }

    private double GetTotalScore()
    {
        if (_stationScores.Count == 0)
        {
            return 0;
        }

        return Math.Round(_stationScores.Average(x => x.FinalScore), 1);
    }

    private void ExportSessionReport()
    {
        var report = new DriveSessionReport
        {
            StartedAt = _sessionStartedAt,
            EndedAt = DateTime.Now,
            DataSource = _usingLiveMemory ? "LiveMemory" : "Debug",
            TotalScore = GetTotalScore(),
            Stops = _stationScores.ToArray()
        };

        var reportsDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        var jsonPath = _driveReportExporter.Export(reportsDirectory, report);
        var csvPath = _driveReportExporter.ExportCsv(reportsDirectory, report);
        _lastDataSourceError = $"Report exported: {Path.GetFileName(jsonPath)}, {Path.GetFileName(csvPath)}";
    }

    private void OpenLatestReport()
    {
        var reportsDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        var latestPath = _driveReportReader.FindLatestJsonReportPath(reportsDirectory);
        if (string.IsNullOrWhiteSpace(latestPath))
        {
            _lastDataSourceError = "No report file found.";
            UpdateDisplay();
            return;
        }

        try
        {
            var report = _driveReportReader.Load(latestPath);
            var viewer = new ReportViewerWindow(report, latestPath);
            viewer.Show();
        }
        catch (Exception ex)
        {
            _lastDataSourceError = $"Open report failed: {ex.Message}";
            UpdateDisplay();
        }
    }

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
            try
            {
                _lastDataSourceError = string.Empty;
                return _memoryDataSource.GetSnapshot();
            }
            catch (Exception ex)
            {
                _lastDataSourceError = ex.Message;
                _usingLiveMemory = false;
                _debugDataSource.StartSession();
            }
        }

        return _debugDataSource.GetSnapshot();
    }

    private void ReloadConfigurations()
    {
        LoadLineConfigurationOptions();

        if (File.Exists(_scoringConfigPath))
        {
            var scoringLoader = new YamlScoringConfigurationLoader();
            _scoringConfiguration = scoringLoader.LoadFromFile(_scoringConfigPath);
        }
        else
        {
            _scoringConfiguration = new ScoringConfiguration();
        }

        _stopScoringService = new StopScoringService(_scoringConfiguration);

        _memoryDataSource?.Dispose();
        _memoryDataSource = null;

        if (File.Exists(_offsetsConfigPath))
        {
            var offsetsLoader = new YamlMemoryOffsetsConfigurationLoader();
            var offsets = offsetsLoader.LoadFromFile(_offsetsConfigPath);
            _memoryDataSource = new ProcessMemoryRealtimeDataSource(offsets);
        }
    }

    private void LoadLineConfigurationOptions()
    {
        var configsDirectory = Path.Combine(AppContext.BaseDirectory, "configs");
        var lineLoader = new YamlLineConfigurationLoader();
        var previousPath = _lineConfigPath;

        _lineConfigOptions.Clear();

        if (Directory.Exists(configsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(configsDirectory, "*.yaml"))
            {
                try
                {
                    var config = lineLoader.LoadFromFile(file);
                    _lineConfigOptions.Add(new LineConfigurationOption
                    {
                        FilePath = file,
                        Configuration = config,
                        DisplayName = $"{config.LineInfo.NameJp} ({Path.GetFileName(file)})"
                    });
                }
                catch
                {
                    // ignore non-line yaml files
                }
            }
        }

        if (_lineConfigOptions.Count == 0 && File.Exists(previousPath))
        {
            var fallbackConfig = lineLoader.LoadFromFile(previousPath);
            _lineConfigOptions.Add(new LineConfigurationOption
            {
                FilePath = previousPath,
                Configuration = fallbackConfig,
                DisplayName = $"{fallbackConfig.LineInfo.NameJp} ({Path.GetFileName(previousPath)})"
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
        _debugDataSource = new DebugRealtimeDataSource(_lineConfiguration.Stations);
        _timelineInitialized = false;
        PopulateServiceOptions();

        if (_sessionRunning && !_usingLiveMemory)
        {
            _debugDataSource.StartSession();
        }
    }

    private void PopulateServiceOptions()
    {
        var currentServiceId = _selectedService?.Train.Id;
        _serviceOptions.Clear();

        var sourceTrainInfos = _lineConfiguration.TrainInfo.Count == 0
            ? [new TrainInfo { Id = "LOCAL", Type = "Local", Terminal = _lineConfiguration.Stations.Last().Id }]
            : _lineConfiguration.TrainInfo;

        foreach (var train in sourceTrainInfos)
        {
            _serviceOptions.Add(new TrainServiceOption
            {
                Train = train,
                DisplayName = $"{ToServiceLabel(train.Type)} ({train.Id})"
            });
        }

        _suppressComboChange = true;
        ServiceTypeComboBox.ItemsSource = null;
        ServiceTypeComboBox.ItemsSource = _serviceOptions;
        ServiceTypeComboBox.SelectedItem = _serviceOptions.FirstOrDefault(x => x.Train.Id == currentServiceId) ?? _serviceOptions.FirstOrDefault();
        _suppressComboChange = false;

        _selectedService = ServiceTypeComboBox.SelectedItem as TrainServiceOption;
    }

    private void ApplyLineColor()
    {
        var color = _lineConfiguration.LineInfo.LineColor;
        if (string.IsNullOrWhiteSpace(color))
        {
            LineColorPreview.Background = new SolidColorBrush(Color.FromRgb(0, 178, 229));
            return;
        }

        try
        {
            var parsed = ColorConverter.ConvertFromString(color);
            if (parsed is Color c)
            {
                LineColorPreview.Background = new SolidColorBrush(c);
                return;
            }
        }
        catch
        {
            // fall back to default line color
        }

        LineColorPreview.Background = new SolidColorBrush(Color.FromRgb(0, 178, 229));
    }

    private string GetServiceTypeDisplayName()
    {
        if (_selectedService is null)
        {
            return "各駅停車";
        }

        return ToServiceLabel(_selectedService.Train.Type);
    }

    private string GetDirectionText(TrainDisplayState state)
    {
        if (_selectedService is null)
        {
            return "--";
        }

        // For non-loop lines, display the terminal station name
        if (!_lineConfiguration.LineInfo.IsLoop)
        {
            var terminal = _lineConfiguration.Stations.FirstOrDefault(x => x.Id == _selectedService.Train.Terminal);
            return terminal is null ? "--" : $"{terminal.NameJp} 行";
        }

        // For loop lines with major stations defined, use them
        if (_selectedService.Train.MajorStationIds is { Count: > 0 })
        {
            return GetLoopLineDirectionByMajorStations(state);
        }

        // Fallback for other loop lines
        var fallbackTerminal = _lineConfiguration.Stations.FirstOrDefault(x => x.Id == _selectedService.Train.Terminal);
        return fallbackTerminal is null ? "--" : $"{fallbackTerminal.NameJp} 行";
    }

    private string GetLoopLineDirectionByMajorStations(TrainDisplayState state)
    {
        var majorStationIds = _selectedService!.Train.MajorStationIds;
        if (majorStationIds is null || majorStationIds.Count == 0)
        {
            return "--";
        }

        // Get major stations in order (don't sort, keep yaml order)
        var majorStations = majorStationIds
            .Select(id => _lineConfiguration.Stations.FirstOrDefault(x => x.Id == id))
            .Where(s => s is not null)
            .Cast<StationInfo>()
            .ToList();

        if (majorStations.Count < 2)
        {
            return "--";
        }

        var majorIndexById = new Dictionary<int, int>();
        for (var i = 0; i < majorStations.Count; i++)
        {
            majorIndexById[majorStations[i].Id] = i;
        }

        var allStations = _lineConfiguration.Stations;
        var allStationsList = allStations.ToList();
        if (allStations.Count == 0)
        {
            return "--";
        }

        if (state.CurrentStopStation is not null)
        {
            // Stopped at a major station: keep progressed direction (do not revert).
            if (majorIndexById.TryGetValue(state.CurrentStopStation.Id, out var currentMajorIndex))
            {
                var advancedMajorIndex = (currentMajorIndex + 1) % majorStations.Count;
                return BuildMajorDirectionText(majorStations, advancedMajorIndex);
            }

            // Stopped at a non-major station: point to upcoming major pair.
            var currentIndex = allStationsList.FindIndex(x => x.Id == state.CurrentStopStation.Id);
            if (currentIndex < 0)
            {
                return "--";
            }

            var searchStartIndex = (currentIndex + 1) % allStations.Count;
            var nextMajorIndex = FindNextMajorIndexInTravelOrder(allStations, searchStartIndex, majorIndexById);
            return nextMajorIndex < 0 ? "--" : BuildMajorDirectionText(majorStations, nextMajorIndex);
        }

        if (state.NextStation is not null)
        {
            // In transit: if next station is a major station, immediately advance one pair.
            if (majorIndexById.TryGetValue(state.NextStation.Id, out var nextStationMajorIndex))
            {
                var advancedMajorIndex = (nextStationMajorIndex + 1) % majorStations.Count;
                return BuildMajorDirectionText(majorStations, advancedMajorIndex);
            }

            var searchStartIndex = allStationsList.FindIndex(x => x.Id == state.NextStation.Id);
            if (searchStartIndex < 0)
            {
                return "--";
            }

            var nextMajorIndex = FindNextMajorIndexInTravelOrder(allStations, searchStartIndex, majorIndexById);
            return nextMajorIndex < 0 ? "--" : BuildMajorDirectionText(majorStations, nextMajorIndex);
        }

        return "--";
    }

    private static int FindNextMajorIndexInTravelOrder(
        IReadOnlyList<StationInfo> stations,
        int startIndex,
        IReadOnlyDictionary<int, int> majorIndexById)
    {
        for (var i = 0; i < stations.Count; i++)
        {
            var station = stations[(startIndex + i) % stations.Count];
            if (majorIndexById.TryGetValue(station.Id, out var majorIndex))
            {
                return majorIndex;
            }
        }

        return -1;
    }

    private static string BuildMajorDirectionText(IReadOnlyList<StationInfo> majorStations, int majorIndex)
    {
        var nextMajorStation = majorStations[majorIndex];
        var nextNextMajorStation = majorStations[(majorIndex + 1) % majorStations.Count];
        return $"{nextMajorStation.NameJp}·{nextNextMajorStation.NameJp} 方面";
    }

    private void RefreshUpcomingStations(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        _upcomingStations.Clear();

        var stopStations = _lineConfiguration.Stations
            .Where(IsStopForSelectedService)
            .ToList();

        if (stopStations.Count == 0)
        {
            return;
        }

        var totalTokens = Math.Max(1, stopStations.Count * 2 - 1);
        UpdateTimelineState(snapshot, state, stopStations, totalTokens);

        var maxStart = Math.Max(0, totalTokens - TimelineVisibleTokenCount);
        _timelineWindowStart = Math.Clamp(_timelineWindowStart, 0, maxStart);

        var futureBrush = GetFutureBrush();
        var finishedBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));
        var activeBrush = new SolidColorBrush(Color.FromRgb(190, 24, 24));

        var endExclusive = Math.Min(totalTokens, _timelineWindowStart + TimelineVisibleTokenCount);
        for (var tokenIndex = _timelineWindowStart; tokenIndex < endExclusive; tokenIndex++)
        {
            var isStation = tokenIndex % 2 == 0;
            var stationIndex = tokenIndex / 2;
            var station = stopStations[Math.Clamp(stationIndex, 0, stopStations.Count - 1)];

            var fill = tokenIndex < _timelineActiveToken
                ? finishedBrush
                : tokenIndex == _timelineActiveToken
                    ? activeBrush
                    : futureBrush;

            _upcomingStations.Add(new UpcomingStationItem
            {
                NameLabel = isStation ? station.NameJp : string.Empty,
                CodeLabel = isStation ? $"{_lineConfiguration.LineInfo.Code}-{station.Number:D2}" : string.Empty,
                ArrowFill = fill,
                ArrowGeometry = BuildArrowGeometry(tokenIndex == 0, isStation),
                ArrowWidth = isStation ? StationArrowWidth : SegmentArrowWidth,
                StationMarkerVisibility = isStation ? Visibility.Visible : Visibility.Collapsed
            });
        }
    }

    private bool IsStopForSelectedService(StationInfo station)
    {
        if (_selectedService is null || string.IsNullOrWhiteSpace(_selectedService.Train.Id))
        {
            return true;
        }

        return station.SkipTrain.All(x => !string.Equals(x, _selectedService.Train.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateTimelineState(
        RealtimeSnapshot snapshot,
        TrainDisplayState state,
        IReadOnlyList<StationInfo> stopStations,
        int totalTokens)
    {
        var currentPos = ResolveTimelineStationPosition(snapshot, stopStations);
        var expectedActiveToken = snapshot.DoorOpen
            ? currentPos * 2
            : Math.Min(totalTokens - 1, currentPos * 2 + 1);

        if (!_timelineInitialized)
        {
            _timelineActiveToken = expectedActiveToken;
            _timelineWindowStart = _timelineActiveToken <= 2 ? 0 : ((Math.Max(0, _timelineActiveToken - 1) / 2) * 2);
            _timelineLastDoorOpen = snapshot.DoorOpen;
            _timelineInitialized = true;
            return;
        }

        // Always align timeline to latest snapshot to avoid drift when game state jumps
        // (e.g. station switching, missed door-edge samples, prolonged driving).
        _timelineActiveToken = expectedActiveToken;
        _timelineWindowStart = _timelineActiveToken <= 2 ? 0 : ((Math.Max(0, _timelineActiveToken - 1) / 2) * 2);
        _timelineLastDoorOpen = snapshot.DoorOpen;
    }

    private static int ResolveTimelineStationPosition(RealtimeSnapshot snapshot, IReadOnlyList<StationInfo> stopStations)
    {
        if (stopStations.Count == 0)
        {
            return 0;
        }

        // First try exact current-station match.
        var exactIndex = stopStations.ToList().FindIndex(x => x.Id == snapshot.NextStationId);
        if (exactIndex >= 0)
        {
            return exactIndex;
        }

        // If current station is not a stopping station for selected service,
        // anchor to the nearest previous stop to keep timeline stable.
        var previousIndex = stopStations
            .Select((station, index) => new { station.Id, index })
            .Where(x => x.Id < snapshot.NextStationId)
            .Select(x => x.index)
            .DefaultIfEmpty(0)
            .Max();

        return Math.Clamp(previousIndex, 0, stopStations.Count - 1);
    }

    private void ResetAnnouncementState(bool doorOpen)
    {
        _announcementDoorStateInitialized = true;
        _announcementPreviousDoorOpen = doorOpen;
        _announcementTargetStationId = null;
        _announcementDepartureStartDistanceMeters = null;
        _announcementPreviousDistanceMeters = 0;
        _lastNextAnnouncementStationId = null;
        _lastApproachAnnouncementStationId = null;
        _announcementPlayer.Stop();
    }

    private void HandleAutoAnnouncements(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (!_sessionRunning || _selectedService is null)
        {
            _announcementDoorStateInitialized = false;
            return;
        }

        if (!_announcementDoorStateInitialized)
        {
            _announcementPreviousDoorOpen = snapshot.DoorOpen;
            _announcementPreviousDistanceMeters = snapshot.CurrentDistanceMeters;
            _announcementDoorStateInitialized = true;
        }

        var doorCloseTransition = _announcementPreviousDoorOpen && !snapshot.DoorOpen;
        var doorOpenTransition = !_announcementPreviousDoorOpen && snapshot.DoorOpen;

        if (doorCloseTransition)
        {
            _announcementTargetStationId = ResolveAnnouncementTargetStationId(state);
            _announcementDepartureStartDistanceMeters = _announcementPreviousDistanceMeters;
            CloseMelodySelectionPanel();
        }

        if (doorOpenTransition)
        {
            _announcementTargetStationId = null;
            _announcementDepartureStartDistanceMeters = null;
            OpenMelodySelectionPanel(state.CurrentStopStation);
        }

        // In debug mode, session often starts with doors already open; ensure panel is shown in that state too.
        if (snapshot.DoorOpen && state.CurrentStopStation is not null)
        {
            var stationChanged = _melodyCurrentStationId != state.CurrentStopStation.Id;
            if (MelodySelectionPanel.Visibility != Visibility.Visible || stationChanged)
            {
                OpenMelodySelectionPanel(state.CurrentStopStation);
            }
        }
        else if (!snapshot.DoorOpen && MelodySelectionPanel.Visibility == Visibility.Visible)
        {
            CloseMelodySelectionPanel();
        }

        if (!snapshot.DoorOpen && _announcementTargetStationId is int staleTargetStationId)
        {
            var targetStation = _lineConfiguration.Stations.FirstOrDefault(x => x.Id == staleTargetStationId);
            if (targetStation is null || !IsStopForSelectedService(targetStation))
            {
                _announcementTargetStationId = ResolveAnnouncementTargetStationId(state);
                _announcementDepartureStartDistanceMeters = _announcementPreviousDistanceMeters;
            }
        }

        if (!snapshot.DoorOpen && _announcementTargetStationId is null)
        {
            _announcementTargetStationId = ResolveAnnouncementTargetStationId(state);
            _announcementDepartureStartDistanceMeters = _announcementPreviousDistanceMeters;
        }

        if (!snapshot.DoorOpen && _announcementTargetStationId is int stationId)
        {
            var departureStartDistance = _announcementDepartureStartDistanceMeters ?? snapshot.CurrentDistanceMeters;
            var traveledDistance = Math.Abs(snapshot.CurrentDistanceMeters - departureStartDistance);
            if (traveledDistance >= NextAnnouncementDepartureDistanceMeters && _lastNextAnnouncementStationId != stationId)
            {
                if (TryPlayStationAnnouncement(stationId, paIndex: 0))
                {
                    _lastNextAnnouncementStationId = stationId;
                }
            }

            var remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;
            if (remainingDistance < ApproachAnnouncementRemainingDistanceMeters && _lastApproachAnnouncementStationId != stationId)
            {
                var played = TryPlayStationAnnouncement(stationId, paIndex: 1);
                if (played)
                {
                    _lastApproachAnnouncementStationId = stationId;
                }
                else if (!HasStationAnnouncement(stationId, paIndex: 1))
                {
                    _lastApproachAnnouncementStationId = stationId;
                }
            }
        }

        _announcementPreviousDoorOpen = snapshot.DoorOpen;
        _announcementPreviousDistanceMeters = snapshot.CurrentDistanceMeters;
    }

    private int? ResolveAnnouncementTargetStationId(TrainDisplayState state)
    {
        var orderedStations = _lineConfiguration.Stations.ToList();
        if (orderedStations.Count == 0)
        {
            return null;
        }

        var anchorIndex = state.CurrentStopStation is not null
            ? orderedStations.FindIndex(x => x.Id == state.CurrentStopStation.Id) + 1
            : state.NextStation is not null
                ? orderedStations.FindIndex(x => x.Id == state.NextStation.Id)
                : -1;

        if (anchorIndex < 0)
        {
            return null;
        }

        // For rapid/express services, state.NextStation can point to a pass-through station.
        // Always target the next actual stopping station for the selected service.
        for (var i = anchorIndex; i < orderedStations.Count; i++)
        {
            if (IsStopForSelectedService(orderedStations[i]))
            {
                return orderedStations[i].Id;
            }
        }

        if (!_lineConfiguration.LineInfo.IsLoop)
        {
            return null;
        }

        for (var i = 0; i < anchorIndex && i < orderedStations.Count; i++)
        {
            if (IsStopForSelectedService(orderedStations[i]))
            {
                return orderedStations[i].Id;
            }
        }

        return null;
    }

    private bool HasStationAnnouncement(int stationId, int paIndex)
    {
        if (_selectedService is null)
        {
            return false;
        }

        var station = _lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
        if (station is null)
        {
            return false;
        }

        var paList = ResolvePaListForService(station, _selectedService.Train.Id);
        return paList is not null && paList.Count > paIndex && !string.IsNullOrWhiteSpace(paList[paIndex]);
    }

    private bool TryPlayStationAnnouncement(int stationId, int paIndex)
    {
        if (_selectedService is null)
        {
            return false;
        }

        var station = _lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
        if (station is null)
        {
            return false;
        }

        var trainId = _selectedService.Train.Id;
        var paList = ResolvePaListForService(station, trainId);
        if (paList is null || paList.Count <= paIndex)
        {
            return false;
        }

        var fileName = paList[paIndex];
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var lineId = string.IsNullOrWhiteSpace(_lineConfiguration.LineInfo.Id)
            ? Path.GetFileNameWithoutExtension(_lineConfigPath)
            : _lineConfiguration.LineInfo.Id;

        var audioPath = Path.Combine(AppContext.BaseDirectory, "audio", lineId, trainId, fileName);
        if (!File.Exists(audioPath))
        {
            _lastDataSourceError = $"Announcement file not found: {audioPath}";
            return false;
        }

        try
        {
            var playbackPath = PrepareNormalizedAnnouncementAudio(audioPath);
            _announcementPlayer.Stop();
            _announcementPlayer.Open(new Uri(playbackPath, UriKind.Absolute));
            _announcementPlayer.Volume = 1.0;
            _announcementPlayer.Position = TimeSpan.Zero;
            _announcementPlayer.Play();
            return true;
        }
        catch (Exception ex)
        {
            _lastDataSourceError = $"Announcement playback failed: {ex.Message}";
            return false;
        }
    }

    private static IReadOnlyList<string>? ResolvePaListForService(StationInfo station, string trainId)
    {
        if (station.Pa.TryGetValue(trainId, out var directMatch))
        {
            return directMatch;
        }

        var caseInsensitiveMatch = station.Pa
            .FirstOrDefault(x => string.Equals(x.Key, trainId, StringComparison.OrdinalIgnoreCase));
        return caseInsensitiveMatch.Value;
    }

    private string PrepareNormalizedAnnouncementAudio(string sourcePath)
    {
        Directory.CreateDirectory(_announcementTempDirectory);

        var versionKey = $"{sourcePath}|{File.GetLastWriteTimeUtc(sourcePath).Ticks}";
        if (_announcementNormalizedPathCache.TryGetValue(versionKey, out var cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionKey)));
        var outputPath = Path.Combine(_announcementTempDirectory, $"{hash}.wav");

        if (!File.Exists(outputPath))
        {
            NormalizeAnnouncementToWav(sourcePath, outputPath);
        }

        _announcementNormalizedPathCache[versionKey] = outputPath;
        return outputPath;
    }

    private static void NormalizeAnnouncementToWav(string sourcePath, string outputPath)
    {
        using var reader = new AudioFileReader(sourcePath);
        var samples = new List<float>(reader.WaveFormat.SampleRate * Math.Max(1, reader.WaveFormat.Channels) * 6);
        var buffer = new float[reader.WaveFormat.SampleRate * Math.Max(1, reader.WaveFormat.Channels)];

        double sumSquares = 0;
        var sampleCount = 0;
        var peak = 0.0;

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var sample = buffer[i];
                samples.Add(sample);
                sumSquares += sample * sample;
                sampleCount++;
                var abs = Math.Abs(sample);
                if (abs > peak)
                {
                    peak = abs;
                }
            }
        }

        if (sampleCount == 0)
        {
            throw new InvalidOperationException("Audio file has no readable samples.");
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        var gain = AnnouncementTargetRms / Math.Max(rms, 0.0001);
        gain = Math.Clamp(gain, AnnouncementMinGain, AnnouncementMaxGain);

        if (peak > 0.0001)
        {
            gain = Math.Min(gain, AnnouncementLimiterThreshold / peak);
        }

        var softClipNormalizer = Math.Tanh(AnnouncementSoftClipDrive);
        var processedPeak = 0.0;

        for (var i = 0; i < samples.Count; i++)
        {
            var boosted = samples[i] * gain;
            var clipped = Math.Tanh(boosted * AnnouncementSoftClipDrive) / softClipNormalizer;
            var clamped = Math.Clamp(clipped, -1.0, 1.0);
            var abs = Math.Abs(clamped);
            if (abs > processedPeak)
            {
                processedPeak = abs;
            }

            samples[i] = (float)clamped;
        }

        var finalScale = processedPeak <= 0.0001
            ? 1.0
            : Math.Min(1.0, AnnouncementLimiterThreshold / processedPeak);

        var outFormat = new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels);
        using var writer = new WaveFileWriter(outputPath, outFormat);
        var byteBuffer = new byte[samples.Count * sizeof(short)];

        for (var i = 0; i < samples.Count; i++)
        {
            var scaled = samples[i] * (float)finalScale;
            var sample16 = (short)Math.Round(Math.Clamp(scaled, -1f, 1f) * short.MaxValue);
            byteBuffer[i * 2] = (byte)(sample16 & 0xFF);
            byteBuffer[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }

        writer.Write(byteBuffer, 0, byteBuffer.Length);
    }

    private void TryCleanupAnnouncementTempDirectory()
    {
        try
        {
            if (!Directory.Exists(_announcementTempDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-12);
            foreach (var file in Directory.EnumerateFiles(_announcementTempDirectory, "*.wav"))
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore cleanup failures; runtime playback should not be blocked.
                }
            }
        }
        catch
        {
            // Ignore cleanup failures; runtime playback should not be blocked.
        }
    }

    private Brush GetFutureBrush()
    {
        if (LineColorPreview.Background is SolidColorBrush solid)
        {
            return solid;
        }

        return new SolidColorBrush(Color.FromRgb(0, 178, 229));
    }

    private static string BuildArrowGeometry(bool isFirstToken, bool isStationToken)
    {
        var width = isStationToken ? StationArrowWidth : SegmentArrowWidth;
        var bodyEndX = width - ArrowTipWidth;
        var midY = ArrowHeight / 2;
        var notch = Math.Min(ArrowNotchDepth, width / 2 - 2);

        return isFirstToken
            ? $"M0,0 L{bodyEndX:0.##},0 L{width:0.##},{midY:0.##} L{bodyEndX:0.##},{ArrowHeight:0.##} L0,{ArrowHeight:0.##} Z"
            : $"M0,0 L{bodyEndX:0.##},0 L{width:0.##},{midY:0.##} L{bodyEndX:0.##},{ArrowHeight:0.##} L0,{ArrowHeight:0.##} L{notch:0.##},{midY:0.##} Z";
    }

    private static string ToServiceLabel(string rawType)
    {
        if (string.Equals(rawType, "Local", StringComparison.OrdinalIgnoreCase))
        {
            return "各駅停車";
        }

        if (string.Equals(rawType, "Rapid", StringComparison.OrdinalIgnoreCase))
        {
            return "快速";
        }

        return rawType;
    }

    private static string ResolveConfigPath(string configsDirectory, string sampleFileName, string preferredFileName)
    {
        var preferredPath = Path.Combine(configsDirectory, preferredFileName);
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        return Path.Combine(configsDirectory, sampleFileName);
    }

    private static string ResolveLineConfigPath(string configsDirectory)
    {
        var preferredPath = Path.Combine(configsDirectory, "keihin-negishi.yaml");
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        var samplePath = Path.Combine(configsDirectory, "keihin-negishi.sample.yaml");
        if (File.Exists(samplePath))
        {
            return samplePath;
        }

        if (!Directory.Exists(configsDirectory))
        {
            return samplePath;
        }

        var loader = new YamlLineConfigurationLoader();
        foreach (var file in Directory.EnumerateFiles(configsDirectory, "*.yaml"))
        {
            try
            {
                loader.LoadFromFile(file);
                return file;
            }
            catch
            {
                // ignore non-line yaml files
            }
        }

        return samplePath;
    }

    private sealed class LineConfigurationOption
    {
        public required string DisplayName { get; init; }

        public required string FilePath { get; init; }

        public required LineConfiguration Configuration { get; init; }
    }

    private sealed class TrainServiceOption
    {
        public required string DisplayName { get; init; }

        public required TrainInfo Train { get; init; }
    }

    private sealed class UpcomingStationItem
    {
        public required string NameLabel { get; init; }

        public required string CodeLabel { get; init; }

        public required Brush ArrowFill { get; init; }

        public required string ArrowGeometry { get; init; }

        public required double ArrowWidth { get; init; }

        public required Visibility StationMarkerVisibility { get; init; }
    }
}
