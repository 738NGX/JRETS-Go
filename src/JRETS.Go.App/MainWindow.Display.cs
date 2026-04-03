using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App;

public partial class MainWindow
{
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
        ApplyMandatoryUpdateGateUi();

        var snapshot = GetCurrentSnapshot();
        _activeLinePath = snapshot.LinePath;
        TrackSessionDistance(snapshot);
        var sourceText = _usingLiveMemory ? "LiveMemory" : "Debug";

        ToggleDebugModeButton.Content = _debugModeEnabled ? "Disable Debug Mode" : "Enable Debug Mode";
        DebugModeStateText.Text = _debugModeEnabled
            ? $"Mode: On ({sourceText})"
            : "Mode: Off";
        DebugActionsPanel.Visibility = _debugModeEnabled ? Visibility.Visible : Visibility.Collapsed;
        ManualSelectionPanel.Visibility = _debugModeEnabled && _manualSelectionEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;

        var hasHudStatus = TryGetHudStatusMessage(out var hudMessage);
        if (hasHudStatus)
        {
            ApplyLineColor();
            ServiceTypeTextBlock.Text = string.Empty;
            ServiceTypeTagBorder.Background = Brushes.Transparent;
            DirectionTextBlock.Text = string.Empty;
            NextStationStatusTextBlock.Text = "";
            NextStationNameTextBlock.Text = hudMessage;
            StationCodeBadgeOuterBorder.Visibility = Visibility.Collapsed;
            StationCode.Text = string.Empty;
            LineCode.Text = string.Empty;
            LineNumberBadgeTextBlock.Text = string.Empty;
            _upcomingStations.Clear();
            ErrorTextBlock.Text = string.IsNullOrWhiteSpace(_lastDataSourceError)
                ? string.Empty
                : $"Info: {_lastDataSourceError}";
            UpdateMemoryDebugText(snapshot);
            return;
        }

        var state = _displayStateResolver.Resolve(_lineConfiguration, snapshot, IsStopForSelectedService, _latchedStationId);
        _latestApproachSnapshot = snapshot;
        _latestApproachState = state;
        CaptureStopScore(snapshot, state);
        UpdateLatchedStationId(snapshot, state);
        var stationContext = BuildStationContext(snapshot, state);

        ApplyLineColor(snapshot.DoorOpen, stationContext.HudDisplayStation, stationContext.MapFromStationId, stationContext.MapToStationId);
        var serviceTypeText = GetServiceTypeDisplayName();
        var hasServiceTypeText = !string.IsNullOrWhiteSpace(serviceTypeText);
        ServiceTypeTextBlock.Text = hasServiceTypeText ? serviceTypeText : string.Empty;
        ServiceTypeTagBorder.Background = hasServiceTypeText ? LineColorPreview.Background : Brushes.Transparent;
        DirectionTextBlock.Text = GetDirectionText(state);

        // Determine display station and status text from shared StationContext.
        var displayStation = stationContext.HudDisplayStation;
        var statusText = stationContext.HudStatusText;

        NextStationStatusTextBlock.Text = statusText;

        var hasStationCode = displayStation is not null && !string.IsNullOrWhiteSpace(displayStation.Code);
        var lineCodeText = ResolveEffectiveLineCode(displayStation);
        var lineNumberText = displayStation is null || displayStation.Number <= 0 ? string.Empty : displayStation.Number.ToString("00");
        var hasLineCode = !string.IsNullOrWhiteSpace(lineCodeText);
        var hasLineNumber = !string.IsNullOrWhiteSpace(lineNumberText);
        var showStationCodeBadge = hasLineCode && hasLineNumber;

        NextStationNameTextBlock.Text = displayStation?.NameJp ?? "--";
        StationCodeBadgeOuterBorder.Visibility = showStationCodeBadge ? Visibility.Visible : Visibility.Collapsed;
        StationCodeBadgeOuterBorder.Background = hasStationCode ? Brushes.Black : Brushes.Transparent;
        StationCode.Text = showStationCodeBadge && hasStationCode ? displayStation!.Code! : string.Empty;
        LineCode.Text = showStationCodeBadge ? lineCodeText : string.Empty;
        LineNumberBadgeTextBlock.Text = showStationCodeBadge ? lineNumberText : string.Empty;

        RefreshUpcomingStations(snapshot, state, stationContext);
        HandleAutoAnnouncements(snapshot, state);
        ApplyScoreSummaryText();
        EnsureApproachRealtimeUpdateTimerActive(snapshot, state, stationContext);

        ErrorTextBlock.Text = string.IsNullOrWhiteSpace(_lastDataSourceError)
            ? string.Empty
            : $"Info: {_lastDataSourceError}";

        UpdateMemoryDebugText(snapshot);
        RequestMapRender(snapshot, state, stationContext);
    }

    private void ApplyMandatoryUpdateGateUi()
    {
        StartSessionButton.IsEnabled = !_mandatoryUpdatePending;
        EndSessionButton.IsEnabled = _sessionRunning;

        if (_mandatoryUpdatePending && string.IsNullOrWhiteSpace(_hudStatusMessage))
        {
            _hudStatusMessage = "检测到强制更新未完成，请先完成更新后再开始运行。";
        }
    }

    private bool TryGetHudStatusMessage(out string message)
    {
        if (!string.IsNullOrWhiteSpace(_hudStatusMessage))
        {
            message = _hudStatusMessage!;
            return true;
        }

        if (!_sessionRunning)
        {
            message = "運転外 / Not Driving";
            return true;
        }

        if (_selectedService is null)
        {
            message = "Error: No service selected";
            return true;
        }

        message = string.Empty;
        return false;
    }

    private void UpdateMemoryDebugText(RealtimeSnapshot snapshot)
    {
        MemNextStationText.Text = $"Current Station Id: {snapshot.NextStationId}";
        MemDoorText.Text = $"Door Open: {(snapshot.DoorOpen ? "True" : "False")}";
        var clockTime = TimeSpan.FromSeconds(snapshot.MainClockSeconds);
        MemMainClockText.Text = $"Main Clock: {clockTime.Hours:D2}:{clockTime.Minutes:D2}:{clockTime.Seconds:D2}";
        MemTimetableText.Text =
            $"Timetable (H:M:S): {snapshot.TimetableHour:D2}:{snapshot.TimetableMinute:D2}:{snapshot.TimetableSecond:D2}";
        MemCurrentDistanceText.Text = $"Current Distance (m): {snapshot.CurrentDistanceMeters:F2}";
        MemTargetDistanceText.Text = $"Target Stop Distance (m): {snapshot.TargetStopDistanceMeters:F2}";
        MemLinePathText.Text = string.IsNullOrWhiteSpace(snapshot.LinePath)
            ? "Line Path: <empty>"
            : $"Line Path: {snapshot.LinePath}";
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
            _activeRunningSegmentMapping = null;
            _mapLastTrainMarkerDistanceMeters = null;
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
            _activeApproachOvershootFaultTriggered = false;
            ResetApproachDisplaySmoothing();
            TryBuildRunningSegmentMapping(snapshot, departureStationId, state.NextStation?.Id);

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
        var scoringStation = state.CurrentStopStation;

        if (!doorOpenTransition || scoringStation is null)
        {
            return;
        }

        var stationContext = BuildStationContext(snapshot, state);
        if (stationContext.Approach.TargetStopDistanceMeters is null || stationContext.Approach.ScheduledSeconds is null)
        {
            return;
        }

        if (_lastScoredStationId == scoringStation.Id)
        {
            return;
        }

        var scoringSnapshot = BuildScoringSnapshot(snapshot, stationContext);
        var stopScore = _stopScoringService.ScoreStop(scoringStation, scoringSnapshot);
        var totalBeforeStop = _runningTotalScore;
        _runningMaxScore += 100;
        _stationScores.Add(stopScore);
        _runningTotalScore = Math.Round(_runningTotalScore + (stopScore.FinalScore ?? 0), 1);
        _lastScoredStationId = scoringStation.Id;
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
        ResetApproachDisplaySmoothing();
        _isApproachPanelPinned = false;

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

        _stationScoreService.UpsertDepartureRecord(
            _stationScores,
            currentStationId.Value,
            currentStationName,
            snapshot.CapturedAt,
            snapshot.MainClockSeconds);
    }

    private RealtimeSnapshot BuildScoringSnapshot(RealtimeSnapshot currentSnapshot, StationContext stationContext)
    {
        return _stationScoreService.BuildScoringSnapshot(
            currentSnapshot,
            stationContext.Approach.TargetStopDistanceMeters,
            stationContext.Approach.ScheduledSeconds,
            stationContext.Approach.OvershootFaultTriggered,
            OvershootFaultMeters);
    }

    private void ApplyScoreSummaryText()
    {
        if (!_isApproachSettlementAnimating)
        {
            ScoreTextBlock.Text = _runningTotalScore.ToString("0.#", CultureInfo.InvariantCulture);
        }

        TotalScoreTextBlock.Text = $"pt/{_runningMaxScore}pt";
    }

    private void UpdateApproachScorePanel(RealtimeSnapshot snapshot, TrainDisplayState state, StationContext stationContext)
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

        if (stationContext.Approach.ScheduledSeconds is null || stationContext.Approach.TargetStopDistanceMeters is null)
        {
            ResetApproachDisplaySmoothing();
            return;
        }

        var remainingMeters = stationContext.Approach.TargetStopDistanceMeters.Value - snapshot.CurrentDistanceMeters;
        if (!_isApproachPanelPinned && remainingMeters >= ApproachPanelTriggerMeters)
        {
            return;
        }

        var scheduledSeconds = stationContext.Approach.ScheduledSeconds.Value;
        var timeErrorSigned = snapshot.MainClockSeconds - scheduledSeconds;
        var distanceErrorSignedMeters = snapshot.CurrentDistanceMeters - stationContext.Approach.TargetStopDistanceMeters.Value;

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

    private void StopScoreCountupAnimation()
    {
        if (_scoreCountupTimer is null)
        {
            return;
        }

        _scoreCountupTimer.Stop();
        _scoreCountupTimer = null;
        _isScoreCountupAnimating = false;
        ScoreTextScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ScoreTextScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ScoreTextScaleTransform.ScaleX = 1;
        ScoreTextScaleTransform.ScaleY = 1;
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

    private void EnsureApproachRealtimeUpdateTimerActive(RealtimeSnapshot snapshot, TrainDisplayState state, StationContext stationContext)
    {
        // Use latched data to determine if should keep running the stop-check updater.
        var shouldBeRunning = _sessionRunning
            && !_isApproachSettlementAnimating
            && !snapshot.DoorOpen
            && stationContext.Approach.ScheduledSeconds is not null
            && stationContext.Approach.TargetStopDistanceMeters is not null
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
                    _latestApproachState = _displayStateResolver.Resolve(_lineConfiguration, liveSnapshot, IsStopForSelectedService, _latchedStationId);
                }

                if (_latestApproachSnapshot is null || _latestApproachState is null)
                {
                    return;
                }

                var liveStationContext = BuildStationContext(_latestApproachSnapshot, _latestApproachState);
                UpdateApproachScorePanel(_latestApproachSnapshot, _latestApproachState, liveStationContext);
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
        StopScoreCountupAnimation();
        _isScoreCountupAnimating = true;
        var startedAt = DateTime.UtcNow;
        _scoreCountupTimer = new DispatcherTimer
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

        _scoreCountupTimer.Tick += (_, _) =>
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

            StopScoreCountupAnimation();
            ScoreTextBlock.Text = toScore.ToString("0.#", CultureInfo.InvariantCulture);
            ApplyScoreSummaryText();
            onCompleted();
        };

        _scoreCountupTimer.Start();
    }

}
