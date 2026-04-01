using System.IO;
using System.Linq;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using JRETS.Go.App.Interop;
using JRETS.Go.App.Services;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;
using JRETS.Go.Core.Services;

namespace JRETS.Go.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private const double NextAnnouncementDepartureDistanceMeters = 200;
    private const double ApproachAnnouncementRemainingDistanceMeters = 400;
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
    private const int HotKeyToggleReportWindow = 1006;
    private const int HotKeyToggleMap = 1007;
    private const int HotKeyToggleMiniMapPanel = 1008;
    private const double MelodyPanelHiddenOffsetX = -340;
    private const double MelodyPanelVisibleOffsetX = 0;
    private static readonly Duration MelodyPanelAnimationDuration = TimeSpan.FromMilliseconds(220);
    private const double ControlPanelHiddenOffsetX = -340;
    private const double ControlPanelVisibleOffsetX = 0;
    private static readonly Duration ControlPanelAnimationDuration = TimeSpan.FromMilliseconds(220);
    private const double MiniMapPanelHiddenOffsetX = 520;
    private const double MiniMapPanelVisibleOffsetX = 0;
    private static readonly Duration MiniMapPanelAnimationDuration = TimeSpan.FromMilliseconds(260);
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
    private readonly string _linePathMappingsConfigPath;
    private readonly string _updateConfigPath;
    private readonly FileSystemWatcher _configWatcher;
    private readonly DispatcherTimer _configReloadDebounceTimer;

    private LineConfiguration _lineConfiguration;
    private DebugRealtimeDataSource _debugDataSource;
    private ProcessMemoryRealtimeDataSource? _memoryDataSource;
    private readonly DisplayStateResolver _displayStateResolver;
    private StopScoringService _stopScoringService;
    private readonly TrainRouteService _trainRouteService = new();
    private readonly AnnouncementRoutingService _announcementRoutingService = new();
    private readonly AnnouncementAudioService _announcementAudioService = new();
    private readonly AnnouncementOrchestrationService _announcementOrchestrationService = new();
    private readonly TimelineService _timelineService = new();
    private readonly StationScoreService _stationScoreService = new();
    private readonly DriveReportExporter _driveReportExporter;
    private readonly DriveReportReader _driveReportReader;
    private readonly DriveReportWorkflowService _driveReportWorkflowService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly ObservableCollection<UpcomingStationItem> _upcomingStations = [];
    private readonly ObservableCollection<MelodyOptionItem> _melodyOptionItems = [];
    private readonly MediaPlayer _announcementPlayer = new();
    private readonly MediaPlayer _melodyPlaybackPlayer = new();
    private readonly ConcurrentDictionary<string, string> _announcementNormalizedPathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LineConfigurationOption> _lineConfigOptions = [];
    private readonly List<TrainServiceOption> _serviceOptions = [];
    private readonly Dictionary<string, LinePathMappingEntry> _linePathMappingByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly AppConfigurationLoader _appConfigurationLoader = new();
    private readonly GitHubReleaseUpdateService _githubReleaseUpdateService = new();
    private readonly LocalReleaseStateStore _localReleaseStateStore = new();
    private readonly ReleaseAssetStagingService _releaseAssetStagingService = new();
    private readonly ChannelUpdateApplyService _channelUpdateApplyService = new();
    private readonly AppUpdateHandoffService _appUpdateHandoffService = new();
    private readonly HashSet<int> _registeredHotKeys = [];
    private static readonly HttpClient _osmHttpClient = CreateOsmHttpClient();
    private readonly Dictionary<string, BitmapImage> _osmTileCache = new(StringComparer.Ordinal);

    private bool _nativeMapBaseLayerVisible = true;
    private bool _isMiniMapPanelVisible;
    private bool _miniMapDataAvailable;
    private StationMapData[]? _currentMapStations;
    private RoutePathData? _currentMapRoute;
    private RunningSegmentMapping? _activeRunningSegmentMapping;
    private bool? _mapLastDoorOpen;
    private int _mapLastCurrentStationId = -1;
    private int _mapLastNextStationId = -1;
    private int _mapLastStopStationId = -1;
    private double? _mapLastTrainMarkerDistanceMeters;
    
    private ReportViewerWindow? _reportViewerWindow;

    private nint _windowHandle;
    private bool _sessionRunning;
    private bool _clickThroughEnabled;
    private bool _debugModeEnabled;
    private bool _usingLiveMemory;
    private bool _manualSelectionEnabled;
    private bool _suppressComboChange;
    private bool _timelineInitialized;
    private bool _timelineLastDoorOpen;
    private int _timelineActiveToken;
    private int _timelineWindowStart;
    private readonly AnnouncementPlaybackState _announcementState = new();
    private string _lastDataSourceError = string.Empty;
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
    private string? _hudStatusMessage;
    private bool _mandatoryUpdatePending;
    private string? _activeLinePath;
    private UpdateConfiguration? _updateConfiguration;
    private CancellationTokenSource? _startupUpdateCheckCancellation;
    private DispatcherTimer? _approachValueAnimationTimer;
    private DispatcherTimer? _approachRealtimeUpdateTimer;
    private DispatcherTimer? _scoreCountupTimer;
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
    private int? _liveStationAnchorId;
    private bool? _lastLiveDoorOpen;

    public MainWindow()
    {
        InitializeComponent();

        var configsDirectory = Path.Combine(AppContext.BaseDirectory, "configs");
        var lineConfigsDirectory = Path.Combine(configsDirectory, "lines");
        _lineConfigPath = _appConfigurationLoader.ResolveLineConfigPath(lineConfigsDirectory);
        _offsetsConfigPath = _appConfigurationLoader.ResolveConfigPath(configsDirectory, "memory-offsets.yaml");
        _linePathMappingsConfigPath = _appConfigurationLoader.ResolveConfigPath(configsDirectory, "lines-path.yaml");
        _updateConfigPath = _appConfigurationLoader.ResolveConfigPath(configsDirectory, "update.yaml");

        var loader = new YamlLineConfigurationLoader();
        _lineConfiguration = loader.LoadFromFile(_lineConfigPath);

        _debugDataSource = new DebugRealtimeDataSource(
            _lineConfiguration.Stations,
            TryLoadDebugStationDisplacementsMeters(_lineConfiguration));
        _displayStateResolver = new DisplayStateResolver();
        _stopScoringService = new StopScoringService();
        _driveReportExporter = new DriveReportExporter();
        _driveReportReader = new DriveReportReader();
        UpcomingStationsItemsControl.ItemsSource = _upcomingStations;
        MelodyOptionsItemsControl.ItemsSource = _melodyOptionItems;

        LineConfigComboBox.DisplayMemberPath = nameof(LineConfigurationOption.DisplayName);
        ServiceTypeComboBox.DisplayMemberPath = nameof(TrainServiceOption.DisplayName);
        EnableManualSelectionCheckBox.IsChecked = false;
        _manualSelectionEnabled = false;

        LoadLineConfigurationOptions();

        ReloadConfigurations();

        _configWatcher = new FileSystemWatcher(Path.Combine(AppContext.BaseDirectory, "configs"), "*.yaml")
        {
            IncludeSubdirectories = true,
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
