using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Animation;
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
    private const double MelodyPanelHiddenOffsetX = -340;
    private const double MelodyPanelVisibleOffsetX = 0;
    private static readonly Duration MelodyPanelAnimationDuration = TimeSpan.FromMilliseconds(220);
    private const double ControlPanelHiddenOffsetX = -340;
    private const double ControlPanelVisibleOffsetX = 0;
    private static readonly Duration ControlPanelAnimationDuration = TimeSpan.FromMilliseconds(220);
    private const double ApproachPanelTriggerMeters = 100;
    private const double ApproachPanelHideTriggerMeters = 150;
    private const int ApproachSettlementStepOneDurationMs = 480;
    private const int ApproachSettlementStepTwoDurationMs = 560;
    private const int ScorePulseDurationMs = 520;
    private const int UiRefreshIntervalMs = 120;
    private const int StopCheckRealtimeRefreshIntervalMs = 16;
    private const int LiveMemorySamplingApproachIntervalMs = 16;
    private const int LiveMemorySamplingCruiseIntervalMs = 120;
    private const int LiveMemorySamplingErrorBackoffIntervalMs = 240;
    private const int LiveMemorySnapshotStaleMs = 500;
    private const int DepartureDoorCloseDebounceMs = 1200;
    private const double LiveMemoryApproachSamplingTriggerMeters = ApproachPanelHideTriggerMeters;
    private const double ApproachDisplaySmoothingHz = 5;
    private const double ApproachDisplayMinAlpha = 0.04;
    private const double ApproachDisplayMaxAlpha = 0.22;
    private const double ApproachDisplayMaxDistanceDeltaMetersPerSecond = 18;
    private const double ApproachDisplayMaxTimeDeltaSecondsPerSecond = 1.4;

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
    private DateTime? _lastDoorOpenTransitionAt;
    private double? _activeApproachTargetStopDistance;
    private int? _activeApproachScheduledSeconds;
    private bool _activeApproachOvershootFaultTriggered;
    private int? _activeApproachStationId;
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
    private bool _isMelodySelectionPanelVisible;
    private bool _isControlPanelVisible = true;
    private int? _sessionTerminalStationId;
    private bool _isApproachPanelVisible;
    private bool _isApproachPanelHideAnimating;
    private bool _isApproachPanelPinned;
    private bool _isApproachSettlementAnimating;
    private bool _isScoreCountupAnimating;
    private double _runningTotalScore;
    private int _runningMaxScore = 0;
    private double _sessionDistanceMeters;
    private double? _lastDistanceSampleMeters;
    private string? _originStationName;
    private int? _lastKnownStopStationId;
    private string? _lastKnownStopStationName;
    private DispatcherTimer? _approachValueAnimationTimer;
    private DispatcherTimer? _approachRealtimeUpdateTimer;
    private bool _scoreCapacityUpdatedForCurrentRun;
    private double? _smoothedApproachDistanceErrorMeters;
    private double? _smoothedApproachTimeErrorSeconds;
    private double? _displayApproachDistanceErrorMeters;
    private double? _displayApproachTimeErrorSeconds;
    private DateTime? _lastApproachSmoothingSampleAt;
    private RealtimeSnapshot? _latestApproachSnapshot;
    private TrainDisplayState? _latestApproachState;
    private readonly object _liveSnapshotSync = new();
    private CancellationTokenSource? _liveMemorySamplingCancellation;
    private Task? _liveMemorySamplingTask;
    private RealtimeSnapshot? _latestLiveMemorySnapshot;
    private DateTime _latestLiveMemorySnapshotAtUtc;
    private string? _liveMemorySamplingLastError;

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
            Interval = TimeSpan.FromMilliseconds(UiRefreshIntervalMs)
        };
        _refreshTimer.Tick += RefreshTimerOnTick;
        _refreshTimer.Start();

        _melodyPlaybackPlayer.MediaEnded += MelodyPlaybackPlayerOnMediaEnded;

        ApplyScoreSummaryText();

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
        StopLiveMemorySampling();
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
        RegisterHotKey(HotKeyMelodyCycleSelection, HotKeyModifiers.NoRepeat, 0x09); // Tab
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
                if (_isMelodySelectionPanelVisible)
                {
                    ToggleMelodyPlayback();
                }

                handled = true;
                break;
            case HotKeyMelodyCycleSelection:
                if (_isMelodySelectionPanelVisible)
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
        AnimateMelodySelectionPanel(show: true);
    }

    private void CloseMelodySelectionPanel()
    {
        AnimateMelodySelectionPanel(show: false);
        _melodyCurrentStationId = null;
        _melodyCurrentOptions.Clear();
        _melodySelectedIndex = 0;
        StopMelodyPlayback();
    }

    private void AnimateMelodySelectionPanel(bool show)
    {
        _isMelodySelectionPanelVisible = show;
        MelodySelectionPanel.Visibility = Visibility.Visible;

        MelodySelectionPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
        MelodySelectionPanel.BeginAnimation(OpacityProperty, null);

        MelodySelectionPanelTransform.X = show ? MelodyPanelHiddenOffsetX : MelodyPanelVisibleOffsetX;
        MelodySelectionPanel.Opacity = show ? 0 : 1;

        var slideAnimation = new DoubleAnimation
        {
            From = MelodySelectionPanelTransform.X,
            To = show ? MelodyPanelVisibleOffsetX : MelodyPanelHiddenOffsetX,
            Duration = MelodyPanelAnimationDuration,
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        var opacityAnimation = new DoubleAnimation
        {
            From = MelodySelectionPanel.Opacity,
            To = show ? 1 : 0,
            Duration = MelodyPanelAnimationDuration
        };

        if (!show)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (!_isMelodySelectionPanelVisible)
                {
                    MelodySelectionPanel.Visibility = Visibility.Collapsed;
                }
            };
        }

        MelodySelectionPanelTransform.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
        MelodySelectionPanel.BeginAnimation(OpacityProperty, opacityAnimation);
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
        var status = _melodyIsPlaying ? "▶ 再生中 / Playing" : "◻ 停止中 / Stopped";
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
        _sessionDistanceMeters = 0;
        _lastDistanceSampleMeters = null;
        _originStationName = null;
        _lastKnownStopStationId = null;
        _lastKnownStopStationName = null;
        _stationScores.Clear();
        _runningTotalScore = 0;
        _runningMaxScore = 0;
        _scoreCapacityUpdatedForCurrentRun = false;
        _lastScoredStationId = null;
        _lastDoorOpenTransitionAt = null;
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
        _latestApproachSnapshot = null;
        _latestApproachState = null;
        ResetApproachDisplaySmoothing();
        _isApproachPanelPinned = false;
        StopApproachValueAnimation();
        HideApproachPanel(immediate: true);
        _sessionTerminalStationId = _selectedService?.Train.Terminal;
        if (_sessionTerminalStationId.HasValue)
        {
            _sessionTerminalStationId = NormalizeStationIdForScoring(_sessionTerminalStationId.Value);
        }
        _usingLiveMemory = TryActivateLiveMemoryMode();
        if (!_usingLiveMemory)
        {
            _debugDataSource.StartSession();
        }
        else
        {
            StartLiveMemorySampling();
        }

        var currentSnapshot = GetCurrentSnapshot();
        var currentState = _displayStateResolver.Resolve(_lineConfiguration, currentSnapshot);
        _originStationName = currentState.CurrentStopStation?.NameJp;
        _lastKnownStopStationId = currentState.CurrentStopStation?.Id;
        _lastKnownStopStationName = currentState.CurrentStopStation?.NameJp;

        // Do NOT latch approach data here; wait for explicit door-close transition to capture next-station reference.
        // This ensures we read the correct target distance at the exact moment of departure.

        _lastDistanceSampleMeters = currentSnapshot.CurrentDistanceMeters;
        _previousDoorOpen = currentSnapshot.DoorOpen;
        if (_previousDoorOpen)
        {
            _lastDoorOpenTransitionAt = currentSnapshot.CapturedAt;
        }
        ResetAnnouncementState(currentSnapshot.DoorOpen);
        ApplyScoreSummaryText();

        SetClickThroughMode(enabled: true);
        AnimateControlPanel(show: false);

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
        StopLiveMemorySampling();
        _lastDistanceSampleMeters = null;
        _lastKnownStopStationId = null;
        _lastKnownStopStationName = null;
        _lastDoorOpenTransitionAt = null;
        _timelineInitialized = false;
        _sessionTerminalStationId = null;
        ResetAnnouncementState(doorOpen: true);
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
        _latestApproachSnapshot = null;
        _latestApproachState = null;
        ResetApproachDisplaySmoothing();
        _isApproachPanelPinned = false;
        StopApproachValueAnimation();
        StopApproachRealtimeUpdateTimer();
        HideApproachPanel(immediate: true);
        CloseMelodySelectionPanel();

        SetClickThroughMode(enabled: false);
        AnimateControlPanel(show: true);

        UpdateDisplay();
    }

    private void ToggleClickThrough()
    {
        SetClickThroughMode(enabled: !_clickThroughEnabled);
        AnimateControlPanel(show: !_clickThroughEnabled);
        UpdateDisplay();
    }

    private void SetClickThroughMode(bool enabled)
    {
        if (_windowHandle == nint.Zero)
        {
            return;
        }

        _clickThroughEnabled = enabled;

        var exStyle = NativeMethods.GetWindowLong(_windowHandle, NativeMethods.GwlExStyle);
        if (enabled)
        {
            exStyle |= NativeMethods.WsExTransparent | NativeMethods.WsExLayered;
        }
        else
        {
            exStyle &= ~NativeMethods.WsExTransparent;
        }

        NativeMethods.SetWindowLong(_windowHandle, NativeMethods.GwlExStyle, exStyle);
    }

    private void AnimateControlPanel(bool show)
    {
        _isControlPanelVisible = show;
        ControlPanel.Visibility = Visibility.Visible;

        ControlPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
        ControlPanel.BeginAnimation(OpacityProperty, null);

        ControlPanelTransform.X = show ? ControlPanelHiddenOffsetX : ControlPanelVisibleOffsetX;
        ControlPanel.Opacity = show ? 0 : 1;

        var slideAnimation = new DoubleAnimation
        {
            From = ControlPanelTransform.X,
            To = show ? ControlPanelVisibleOffsetX : ControlPanelHiddenOffsetX,
            Duration = ControlPanelAnimationDuration,
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        var opacityAnimation = new DoubleAnimation
        {
            From = ControlPanel.Opacity,
            To = show ? 1 : 0,
            Duration = ControlPanelAnimationDuration
        };

        if (!show)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (!_isControlPanelVisible)
                {
                    ControlPanel.Visibility = Visibility.Collapsed;
                }
            };
        }

        ControlPanelTransform.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
        ControlPanel.BeginAnimation(OpacityProperty, opacityAnimation);
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
        TrackSessionDistance(snapshot);
        var state = _displayStateResolver.Resolve(_lineConfiguration, snapshot);
        _latestApproachSnapshot = snapshot;
        _latestApproachState = state;
        CaptureStopScore(snapshot, state);

        var sourceText = _usingLiveMemory ? "LiveMemory" : "Debug";

        ApplyLineColor();
        ServiceTypeTextBlock.Text = GetServiceTypeDisplayName();
        DirectionTextBlock.Text = GetDirectionText(state);

        // Determine display station and status text based on door and distance
        StationInfo? displayStation = null;
        string statusText = "次は";
        var nextStoppingStation = ResolveAnnouncementTargetStationId(state) is int nextStoppingStationId
            ? _lineConfiguration.Stations.FirstOrDefault(x => x.Id == nextStoppingStationId)
            : null;

        if (snapshot.DoorOpen && state.CurrentStopStation is not null)
        {
            // ただいま (currently stopped at this station)
            displayStation = state.CurrentStopStation;
            statusText = "ただいま";
        }
        else if (!snapshot.DoorOpen && nextStoppingStation is not null)
        {
            // Check remaining distance to next stopping station
            var remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;
            if (remainingDistance < 100)
            {
                // まもなく (approaching next stop - less than 100m away)
                displayStation = nextStoppingStation;
                statusText = "まもなく";
            }
            else
            {
                // 次は (normal state - more than 400m away)
                displayStation = nextStoppingStation;
                statusText = "次は";
            }
        }
        else
        {
            // Fallback to physical next station when stopping-station resolution is unavailable.
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
        ApplyScoreSummaryText();
        EnsureApproachRealtimeUpdateTimerActive(snapshot, state);

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

    private void TrackSessionDistance(RealtimeSnapshot snapshot)
    {
        if (!_sessionRunning)
        {
            _lastDistanceSampleMeters = null;
            return;
        }

        if (_lastDistanceSampleMeters is null)
        {
            _lastDistanceSampleMeters = snapshot.CurrentDistanceMeters;
            return;
        }

        var delta = Math.Abs(snapshot.CurrentDistanceMeters - _lastDistanceSampleMeters.Value);
        if (delta > 0 && delta < 5000)
        {
            _sessionDistanceMeters += delta;
        }

        _lastDistanceSampleMeters = snapshot.CurrentDistanceMeters;
    }

    private void CaptureStopScore(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (!_sessionRunning)
        {
            _previousDoorOpen = snapshot.DoorOpen;
            return;
        }

        if (snapshot.DoorOpen && state.CurrentStopStation is not null)
        {
            _lastKnownStopStationId = state.CurrentStopStation.Id;
            _lastKnownStopStationName = state.CurrentStopStation.NameJp;
        }

        var doorOpenTransition = !_previousDoorOpen && snapshot.DoorOpen;
        var doorCloseTransition = _previousDoorOpen && !snapshot.DoorOpen;

        if (doorOpenTransition)
        {
            _lastDoorOpenTransitionAt = snapshot.CapturedAt;
        }

        var ignoreCloseAsGlitch = false;
        if (doorCloseTransition && _lastDoorOpenTransitionAt is DateTime lastDoorOpenAt)
        {
            var elapsedMs = (snapshot.CapturedAt - lastDoorOpenAt).TotalMilliseconds;
            if (elapsedMs >= 0 && elapsedMs < DepartureDoorCloseDebounceMs)
            {
                ignoreCloseAsGlitch = true;
            }
        }

        if (doorCloseTransition && !ignoreCloseAsGlitch)
        {
            CaptureStationDeparture(snapshot, state);
            _isApproachPanelPinned = false;

            // Latch next-stop targets at departure start and keep them until next stop scoring.
            // At departure time: TargetStopDistanceMeters and TimetableXXX already point to the next stopping station.
            // Resolve the departure station from stable tracked state, then derive the next stop.
            // At door-close tick, state.CurrentStopStation can already be null.
            var departureStationId = state.CurrentStopStation?.Id ?? _lastKnownStopStationId;
            _activeApproachTargetStopDistance = snapshot.TargetStopDistanceMeters;
            _activeApproachScheduledSeconds = snapshot.TimetableHour * 3600 + snapshot.TimetableMinute * 60 + snapshot.TimetableSecond;
            _activeApproachStationId = ResolveNextStoppingStationFromCurrentId(departureStationId);
            _activeApproachOvershootFaultTriggered = false;
            ResetApproachDisplaySmoothing();

            if (!_scoreCapacityUpdatedForCurrentRun)
            {
                _runningMaxScore += 100;
                _scoreCapacityUpdatedForCurrentRun = true;
            }
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

        // Scoring station must come from latched departure-time reference data.
        // This avoids scoring against transient/incorrect live station IDs at door-open transitions.
        var normalizedApproachStationId = _activeApproachStationId.HasValue
            ? NormalizeStationIdForScoring(_activeApproachStationId.Value)
            : (int?)null;
        StationInfo? scoringStation = normalizedApproachStationId.HasValue
            ? _lineConfiguration.Stations.FirstOrDefault(x => x.Id == normalizedApproachStationId.Value)
            : null;

        if (!doorOpenTransition || scoringStation is null)
        {
            return;
        }

        if (_activeApproachTargetStopDistance is null || _activeApproachScheduledSeconds is null)
        {
            return;
        }

        if (_lastScoredStationId == scoringStation.Id)
        {
            return;
        }

        var scoringSnapshot = BuildScoringSnapshot(snapshot);
        var stopScore = _stopScoringService.ScoreStop(scoringStation, scoringSnapshot);
        var totalBeforeStop = _runningTotalScore;
        _stationScores.Add(stopScore);
        _runningTotalScore = Math.Round(_runningTotalScore + (stopScore.FinalScore ?? 0), 1);
        _lastScoredStationId = scoringStation.Id;
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
        ResetApproachDisplaySmoothing();
        _isApproachPanelPinned = false;
        _scoreCapacityUpdatedForCurrentRun = false;

        BeginStopSettlementAnimation(stopScore, totalBeforeStop, _runningTotalScore);

        // Auto-end session when reaching terminal station
        if (_sessionTerminalStationId.HasValue
            && NormalizeStationIdForScoring(scoringStation.Id) == _sessionTerminalStationId.Value)
        {
            EndSession();
        }
    }

    private void CaptureStationDeparture(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        var currentStationId = state.CurrentStopStation?.Id ?? _lastKnownStopStationId;
        var currentStationName = state.CurrentStopStation?.NameJp ?? _lastKnownStopStationName;
        if (!currentStationId.HasValue || string.IsNullOrWhiteSpace(currentStationName))
        {
            return;
        }

        _originStationName ??= currentStationName;
        _lastKnownStopStationId = currentStationId;
        _lastKnownStopStationName = currentStationName;

        var departureSeconds = snapshot.MainClockSeconds;

        for (var i = _stationScores.Count - 1; i >= 0; i--)
        {
            var row = _stationScores[i];
            if (row.StationId != currentStationId.Value)
            {
                continue;
            }

            if (!row.IsScoredStop)
            {
                _stationScores[i] = new StationStopScore
                {
                    StationId = row.StationId,
                    StationName = row.StationName,
                    CapturedAt = row.CapturedAt,
                    ScheduledArrivalSeconds = row.ScheduledArrivalSeconds,
                    ActualArrivalSeconds = row.ActualArrivalSeconds,
                    ActualDepartureSeconds = departureSeconds,
                    PositionErrorMeters = row.PositionErrorMeters,
                    TimeErrorSeconds = row.TimeErrorSeconds,
                    PositionScore = row.PositionScore,
                    TimeScore = row.TimeScore,
                    FinalScore = row.FinalScore,
                    IsScoredStop = row.IsScoredStop
                };
                return;
            }

            if (row.ActualDepartureSeconds.HasValue)
            {
                continue;
            }

            _stationScores[i] = new StationStopScore
            {
                StationId = row.StationId,
                StationName = row.StationName,
                CapturedAt = row.CapturedAt,
                ScheduledArrivalSeconds = row.ScheduledArrivalSeconds,
                ActualArrivalSeconds = row.ActualArrivalSeconds,
                ActualDepartureSeconds = departureSeconds,
                PositionErrorMeters = row.PositionErrorMeters,
                TimeErrorSeconds = row.TimeErrorSeconds,
                PositionScore = row.PositionScore,
                TimeScore = row.TimeScore,
                FinalScore = row.FinalScore,
                IsScoredStop = row.IsScoredStop
            };
            return;
        }

        _stationScores.Add(new StationStopScore
        {
            StationId = currentStationId.Value,
            StationName = currentStationName,
            CapturedAt = snapshot.CapturedAt,
            ScheduledArrivalSeconds = null,
            ActualArrivalSeconds = null,
            ActualDepartureSeconds = departureSeconds,
            PositionErrorMeters = null,
            TimeErrorSeconds = null,
            PositionScore = null,
            TimeScore = null,
            FinalScore = null,
            IsScoredStop = false
        });
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

    private void ApplyScoreSummaryText()
    {
        if (!_isApproachSettlementAnimating)
        {
            ScoreTextBlock.Text = _runningTotalScore.ToString("0.#", CultureInfo.InvariantCulture);
        }

        TotalScoreTextBlock.Text = $"pt/{_runningMaxScore}pt";
    }

    private void UpdateApproachScorePanel(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (!_sessionRunning)
        {
            ResetApproachDisplaySmoothing();
            HideApproachPanel(immediate: true);
            return;
        }

        if (_isApproachSettlementAnimating)
        {
            return;
        }

        if (snapshot.DoorOpen)
        {
            ResetApproachDisplaySmoothing();
            return;
        }

        if (_activeApproachStationId is null)
        {
            ResetApproachDisplaySmoothing();
            return;
        }

        if (_activeApproachScheduledSeconds is null || _activeApproachTargetStopDistance is null)
        {
            ResetApproachDisplaySmoothing();
            return;
        }

        var remainingMeters = _activeApproachTargetStopDistance.Value - snapshot.CurrentDistanceMeters;
        if (!_isApproachPanelPinned && remainingMeters >= ApproachPanelTriggerMeters)
        {
            return;
        }

        var scheduledSeconds = _activeApproachScheduledSeconds.Value;
        var timeErrorSigned = snapshot.MainClockSeconds - scheduledSeconds;
        var distanceErrorSignedMeters = snapshot.CurrentDistanceMeters - _activeApproachTargetStopDistance.Value;

        var sampleDtSeconds = StopCheckRealtimeRefreshIntervalMs / 1000d;
        var sampleNow = DateTime.UtcNow;
        if (_lastApproachSmoothingSampleAt is DateTime previousSampleAt)
        {
            var dt = (sampleNow - previousSampleAt).TotalSeconds;
            if (dt > 0)
            {
                sampleDtSeconds = dt;
            }
        }

        sampleDtSeconds = Math.Clamp(sampleDtSeconds, 1d / 240d, 0.12d);
        _lastApproachSmoothingSampleAt = sampleNow;

        var alpha = 1d - Math.Exp(-ApproachDisplaySmoothingHz * sampleDtSeconds);
        alpha = Math.Clamp(alpha, ApproachDisplayMinAlpha, ApproachDisplayMaxAlpha);

        _smoothedApproachDistanceErrorMeters = _smoothedApproachDistanceErrorMeters is null
            ? distanceErrorSignedMeters
            : _smoothedApproachDistanceErrorMeters.Value + (distanceErrorSignedMeters - _smoothedApproachDistanceErrorMeters.Value) * alpha;

        _smoothedApproachTimeErrorSeconds = _smoothedApproachTimeErrorSeconds is null
            ? timeErrorSigned
            : _smoothedApproachTimeErrorSeconds.Value + (timeErrorSigned - _smoothedApproachTimeErrorSeconds.Value) * alpha;

        var maxDistanceDelta = ApproachDisplayMaxDistanceDeltaMetersPerSecond * sampleDtSeconds;
        var maxTimeDelta = ApproachDisplayMaxTimeDeltaSecondsPerSecond * sampleDtSeconds;

        _displayApproachDistanceErrorMeters = _displayApproachDistanceErrorMeters is null
            ? _smoothedApproachDistanceErrorMeters.Value
            : MoveTowards(_displayApproachDistanceErrorMeters.Value, _smoothedApproachDistanceErrorMeters.Value, maxDistanceDelta);

        _displayApproachTimeErrorSeconds = _displayApproachTimeErrorSeconds is null
            ? _smoothedApproachTimeErrorSeconds.Value
            : MoveTowards(_displayApproachTimeErrorSeconds.Value, _smoothedApproachTimeErrorSeconds.Value, maxTimeDelta);

        var displayDistanceErrorSignedMeters = _displayApproachDistanceErrorMeters.Value;
        var displayTimeErrorSigned = _displayApproachTimeErrorSeconds.Value;

        var absoluteDistanceMeters = Math.Abs(displayDistanceErrorSignedMeters);
        var distanceDisplay = absoluteDistanceMeters > 100
            ? BuildApproachDisplayValue(absoluteDistanceMeters, displayDistanceErrorSignedMeters > 0)
            : BuildApproachDisplayValue(absoluteDistanceMeters * 100, displayDistanceErrorSignedMeters > 0);
        var distanceUnit = absoluteDistanceMeters > 100 ? "m" : "cm";

        var timeDisplay = BuildApproachDisplayValue(Math.Abs(displayTimeErrorSigned), displayTimeErrorSigned > 0);

        SetApproachPanelValues(distanceDisplay, distanceUnit, timeDisplay, "s");
        _isApproachPanelPinned = true;
        ShowApproachPanel();
    }

    private void ResetApproachDisplaySmoothing()
    {
        _smoothedApproachDistanceErrorMeters = null;
        _smoothedApproachTimeErrorSeconds = null;
        _displayApproachDistanceErrorMeters = null;
        _displayApproachTimeErrorSeconds = null;
        _lastApproachSmoothingSampleAt = null;
    }

    private static double MoveTowards(double current, double target, double maxDelta)
    {
        if (maxDelta <= 0)
        {
            return current;
        }

        var delta = target - current;
        if (Math.Abs(delta) <= maxDelta)
        {
            return target;
        }

        return current + Math.Sign(delta) * maxDelta;
    }

    private static string BuildApproachDisplayValue(double magnitude, bool needsPlusSign)
    {
        var rounded = Math.Round(magnitude, 0);
        var valueText = rounded.ToString("0", CultureInfo.InvariantCulture);
        return needsPlusSign ? $"+{valueText}" : valueText;
    }

    private void SetApproachPanelValues(string distanceValueText, string distanceUnit, string timeValueText, string timeUnit)
    {
        ApproachDistanceValueTextBlock.Text = distanceValueText;
        ApproachDistanceUnitTextBlock.Text = distanceUnit;
        ApproachTimeValueTextBlock.Text = timeValueText;
        ApproachTimeUnitTextBlock.Text = timeUnit;
    }

    private void ShowApproachPanel()
    {
        if (_isApproachPanelVisible
            && ApproachScorePanel.Visibility == Visibility.Visible
            && !_isApproachPanelHideAnimating)
        {
            return;
        }

        _isApproachPanelVisible = true;
        _isApproachPanelHideAnimating = false;
        ApproachScorePanel.Visibility = Visibility.Visible;

        // If a hide animation is still running, cancel it so settlement animation can take over.
        ApproachScorePanel.BeginAnimation(OpacityProperty, null);
        ApproachScorePanelTransform.BeginAnimation(TranslateTransform.YProperty, null);

        var fromOpacity = Math.Clamp(ApproachScorePanel.Opacity, 0, 1);
        var fromY = ApproachScorePanelTransform.Y;

        ApproachScorePanel.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = fromOpacity,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        ApproachScorePanelTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = fromY,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void HideApproachPanel(bool immediate)
    {
        if (!immediate && _isScoreCountupAnimating)
        {
            return;
        }

        if (!immediate && _isApproachPanelHideAnimating)
        {
            return;
        }

        if (!_isApproachPanelVisible && ApproachScorePanel.Visibility != Visibility.Visible)
        {
            return;
        }

        if (immediate)
        {
            _isApproachPanelVisible = false;
            _isApproachPanelHideAnimating = false;
            ApproachScorePanel.BeginAnimation(OpacityProperty, null);
            ApproachScorePanelTransform.BeginAnimation(TranslateTransform.YProperty, null);
            ApproachScorePanel.Opacity = 0;
            ApproachScorePanelTransform.Y = 24;
            ApproachScorePanel.Visibility = Visibility.Collapsed;
            return;
        }

        _isApproachPanelHideAnimating = true;

        var fadeOut = new DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(210),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            if (_isApproachSettlementAnimating)
            {
                return;
            }

            _isApproachPanelVisible = false;
            _isApproachPanelHideAnimating = false;
            ApproachScorePanel.Visibility = Visibility.Collapsed;
            ApproachScorePanelTransform.Y = 24;
        };

        ApproachScorePanel.BeginAnimation(OpacityProperty, fadeOut);
        ApproachScorePanelTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = 20,
            Duration = TimeSpan.FromMilliseconds(210),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
    }

    private void StopApproachValueAnimation()
    {
        if (_approachValueAnimationTimer is null)
        {
            return;
        }

        _approachValueAnimationTimer.Stop();
        _approachValueAnimationTimer = null;
    }

    private void StopApproachRealtimeUpdateTimer()
    {
        if (_approachRealtimeUpdateTimer is null)
        {
            return;
        }

        _approachRealtimeUpdateTimer.Stop();
        _approachRealtimeUpdateTimer = null;
    }

    private void EnsureApproachRealtimeUpdateTimerActive(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        // Use latched data to determine if should keep running the stop-check updater.
        var shouldBeRunning = _sessionRunning
            && !_isApproachSettlementAnimating
            && !snapshot.DoorOpen
            && _activeApproachStationId is not null
            && _activeApproachScheduledSeconds is not null
            && _activeApproachTargetStopDistance is not null
            && !_isScoreCountupAnimating;

        if (shouldBeRunning && _approachRealtimeUpdateTimer is null)
        {
            _approachRealtimeUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(StopCheckRealtimeRefreshIntervalMs)
            };
            _approachRealtimeUpdateTimer.Tick += (_, _) =>
            {
                if (_sessionRunning && _usingLiveMemory && TryGetLatestLiveMemorySnapshot(out var liveSnapshot))
                {
                    _latestApproachSnapshot = liveSnapshot;
                    _latestApproachState = _displayStateResolver.Resolve(_lineConfiguration, liveSnapshot);
                }

                if (_latestApproachSnapshot is null || _latestApproachState is null)
                {
                    return;
                }

                UpdateApproachScorePanel(_latestApproachSnapshot, _latestApproachState);
            };
            _approachRealtimeUpdateTimer.Start();
        }
        else if (!shouldBeRunning && _approachRealtimeUpdateTimer is not null)
        {
            StopApproachRealtimeUpdateTimer();
        }
    }

    private void BeginStopSettlementAnimation(StationStopScore stopScore, double totalBefore, double totalAfter)
    {
        StopApproachValueAnimation();
        _isApproachSettlementAnimating = true;
        ShowApproachPanel();

        var signedPositionError = stopScore.PositionErrorMeters ?? 0;
        var signedTimeError = stopScore.TimeErrorSeconds ?? 0;
        var startDistance = Math.Abs(signedPositionError) * 100;
        var startTime = Math.Abs(signedTimeError);
        var distancePrefix = signedPositionError > 0 ? "+" : string.Empty;
        var timePrefix = signedTimeError > 0 ? "+" : string.Empty;

        AnimateApproachValues(
            startDistance,
            0,
            startTime,
            0,
            ApproachSettlementStepOneDurationMs,
            distancePrefix,
            timePrefix,
            "cm",
            "s",
            () =>
            {
                SetApproachPanelValues("0", "pt", "0", "pt");

                AnimateApproachValues(
                    0,
                    stopScore.PositionScore ?? 0,
                    0,
                    stopScore.TimeScore ?? 0,
                    ApproachSettlementStepTwoDurationMs,
                    string.Empty,
                    string.Empty,
                    "pt",
                    "pt",
                    () =>
                    {
                        AnimateScorePulseAndCountup(totalBefore, totalAfter, () =>
                        {
                            _isApproachSettlementAnimating = false;
                            HideApproachPanel(immediate: false);
                        });
                    });
            });
    }

    private void AnimateApproachValues(
        double fromDistance,
        double toDistance,
        double fromTime,
        double toTime,
        int durationMs,
        string distancePrefix,
        string timePrefix,
        string distanceUnit,
        string timeUnit,
        Action onCompleted)
    {
        StopApproachValueAnimation();

        var startedAt = DateTime.UtcNow;
        _approachValueAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        _approachValueAnimationTimer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startedAt).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);

            var distanceNow = fromDistance + (toDistance - fromDistance) * eased;
            var timeNow = fromTime + (toTime - fromTime) * eased;

            var distanceText = $"{distancePrefix}{Math.Round(distanceNow, 0).ToString("0", CultureInfo.InvariantCulture)}";
            var timeText = $"{timePrefix}{Math.Round(timeNow, 0).ToString("0", CultureInfo.InvariantCulture)}";
            SetApproachPanelValues(distanceText, distanceUnit, timeText, timeUnit);

            if (progress < 1)
            {
                return;
            }

            StopApproachValueAnimation();
            onCompleted();
        };

        _approachValueAnimationTimer.Start();
    }

    private void AnimateScorePulseAndCountup(double fromScore, double toScore, Action onCompleted)
    {
        _isScoreCountupAnimating = true;
        var startedAt = DateTime.UtcNow;
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };

        var pulseAnimation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(ScorePulseDurationMs)
        };
        pulseAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        pulseAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1.22, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180))));
        pulseAnimation.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ScorePulseDurationMs))));

        ScoreTextScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pulseAnimation);
        ScoreTextScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pulseAnimation);

        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - startedAt).TotalMilliseconds;
            var progress = Math.Clamp(elapsed / ScorePulseDurationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);
            var scoreNow = fromScore + (toScore - fromScore) * eased;
            ScoreTextBlock.Text = scoreNow.ToString("0.#", CultureInfo.InvariantCulture);

            if (progress < 1)
            {
                return;
            }

            timer.Stop();
            ScoreTextBlock.Text = toScore.ToString("0.#", CultureInfo.InvariantCulture);
            _isScoreCountupAnimating = false;
            ApplyScoreSummaryText();
            onCompleted();
        };

        timer.Start();
    }

    private double GetTotalScore()
    {
        if (_stationScores.Count == 0)
        {
            return 0;
        }

        return Math.Round(_stationScores.Sum(x => x.FinalScore ?? 0), 1);
    }

    private void ExportSessionReport()
    {
        var currentSnapshot = GetCurrentSnapshot();
        var currentState = _displayStateResolver.Resolve(_lineConfiguration, currentSnapshot);

        var firstStation = _originStationName ?? _stationScores.FirstOrDefault()?.StationName;
        var lastStation = _stationScores.LastOrDefault()?.StationName;
        var segmentText = string.IsNullOrWhiteSpace(firstStation) || string.IsNullOrWhiteSpace(lastStation)
            ? string.Empty
            : $"{firstStation} -> {lastStation}";

        var defaultDirection = string.Equals(_lineConfiguration.LineInfo.Id, "yamanote", StringComparison.OrdinalIgnoreCase)
            ? "内回り"
            : GetDirectionText(currentState);

        var report = new DriveSessionReport
        {
            StartedAt = _sessionStartedAt,
            EndedAt = DateTime.Now,
            DataSource = _usingLiveMemory ? "LiveMemory" : "Debug",
            TotalScore = GetTotalScore(),
            DistanceMeters = Math.Round(_sessionDistanceMeters, 1),
            Metadata = new DriveSessionMetadata
            {
                TrainNumber = _selectedService?.Train.Id,
                ServiceType = _selectedService?.Train.Type,
                LineId = _lineConfiguration.LineInfo.Id,
                LineCode = _lineConfiguration.LineInfo.Code,
                LineName = _lineConfiguration.LineInfo.NameJp,
                LineColor = _lineConfiguration.LineInfo.LineColor,
                DirectionText = defaultDirection,
                SegmentText = segmentText,
                OriginStationName = _originStationName
            },
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
            var configsDirectory = Path.Combine(AppContext.BaseDirectory, "configs");
            var viewer = new ReportViewerWindow(report, latestPath, reportsDirectory, configsDirectory);
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

        StopLiveMemorySampling();
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
            var isFirstVisibleStation = isStation && stationIndex == _timelineWindowStart / 2;
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
                ArrowGeometry = BuildArrowGeometry(isFirstVisibleStation, isStation),
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

        if (!IsWithinSelectedServiceRoute(station))
        {
            return false;
        }

        return station.SkipTrain.All(x => !string.Equals(x, _selectedService.Train.Id, StringComparison.OrdinalIgnoreCase));
    }

    private bool IsWithinSelectedServiceRoute(StationInfo station)
    {
        if (_selectedService is null || _lineConfiguration.LineInfo.IsLoop)
        {
            return true;
        }

        var terminalIndex = GetStationIndexInConfiguredOrder(_selectedService.Train.Terminal);
        if (terminalIndex < 0)
        {
            return true;
        }

        var stationIndex = GetStationIndexInConfiguredOrder(station.Id);
        if (stationIndex < 0)
        {
            return false;
        }

        return stationIndex <= terminalIndex;
    }

    private int GetStationIndexInConfiguredOrder(int stationId)
    {
        for (var i = 0; i < _lineConfiguration.Stations.Count; i++)
        {
            if (_lineConfiguration.Stations[i].Id == stationId)
            {
                return i;
            }
        }

        return -1;
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
            if (!_isMelodySelectionPanelVisible || stationChanged)
            {
                OpenMelodySelectionPanel(state.CurrentStopStation);
            }
        }
        else if (!snapshot.DoorOpen && _isMelodySelectionPanelVisible)
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
            var approachTriggerDistance = ResolveAnnouncementTriggerDistanceMeters(
                stationId,
                paIndex: 1,
                defaultDistanceMeters: ApproachAnnouncementRemainingDistanceMeters);
            if (remainingDistance < approachTriggerDistance && _lastApproachAnnouncementStationId != stationId)
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
        var orderedStations = _lineConfiguration.Stations
            .Where(IsWithinSelectedServiceRoute)
            .ToList();
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
                return NormalizeStationIdForScoring(orderedStations[i].Id);
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
                return NormalizeStationIdForScoring(orderedStations[i].Id);
            }
        }

        return null;
    }

    private int NormalizeStationIdForScoring(int stationId)
    {
        var stations = _lineConfiguration.Stations;
        var station = stations.FirstOrDefault(s => s.Id == stationId);
        if (station is null)
        {
            return stationId;
        }

        // Loop lines can contain duplicate physical stations with different IDs at seam boundaries.
        // Normalize to the first configured occurrence to keep scoring/terminal checks consistent.
        for (var i = 0; i < stations.Count; i++)
        {
            var candidate = stations[i];
            if (!string.Equals(candidate.NameJp, station.NameJp, StringComparison.Ordinal))
            {
                continue;
            }

            if (candidate.Number != station.Number)
            {
                continue;
            }

            var candidateCode = candidate.Code ?? string.Empty;
            var stationCode = station.Code ?? string.Empty;
            if (!string.Equals(candidateCode, stationCode, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate.Id;
        }

        return stationId;
    }

    private int? ResolveNextStoppingStationFromCurrent(StationInfo? currentStation)
    {
        if (currentStation is null)
        {
            return null;
        }

        var allStations = _lineConfiguration.Stations.ToList();
        var currentIndex = allStations.FindIndex(s => s.Id == currentStation.Id);
        if (currentIndex < 0)
        {
            return null;
        }

        // Find the next stopping station after current station
        for (var i = currentIndex + 1; i < allStations.Count; i++)
        {
            if (IsStopForSelectedService(allStations[i]))
            {
                return NormalizeStationIdForScoring(allStations[i].Id);
            }
        }

        // For loop lines, wrap around
        if (_lineConfiguration.LineInfo.IsLoop)
        {
            for (var i = 0; i < currentIndex; i++)
            {
                if (IsStopForSelectedService(allStations[i]))
                {
                    return NormalizeStationIdForScoring(allStations[i].Id);
                }
            }
        }

        return null;
    }

    private int? ResolveNextStoppingStationFromCurrentId(int? currentStationId)
    {
        if (!currentStationId.HasValue)
        {
            return null;
        }

        var currentStation = _lineConfiguration.Stations.FirstOrDefault(s => s.Id == currentStationId.Value);
        return ResolveNextStoppingStationFromCurrent(currentStation);
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
        return paList is not null
            && paList.Count > paIndex
            && !string.IsNullOrWhiteSpace(paList[paIndex].FileName);
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

        var fileName = paList[paIndex].FileName;
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

    private double ResolveAnnouncementTriggerDistanceMeters(int stationId, int paIndex, double defaultDistanceMeters)
    {
        if (_selectedService is null)
        {
            return defaultDistanceMeters;
        }

        var station = _lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
        if (station is null)
        {
            return defaultDistanceMeters;
        }

        var paList = ResolvePaListForService(station, _selectedService.Train.Id);
        if (paList is null || paList.Count <= paIndex)
        {
            return defaultDistanceMeters;
        }

        return paList[paIndex].TriggerDistanceMeters ?? defaultDistanceMeters;
    }

    private static IReadOnlyList<PaAnnouncementEntry>? ResolvePaListForService(StationInfo station, string trainId)
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
