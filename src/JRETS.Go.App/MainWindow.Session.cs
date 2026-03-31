using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using JRETS.Go.App.Interop;

namespace JRETS.Go.App;

public partial class MainWindow
{
    private void StartSession()
    {
        if (_mandatoryUpdatePending)
        {
            _sessionRunning = false;
            _usingLiveMemory = false;
            StopLiveMemorySampling();
            _hudStatusMessage = "检测到强制更新未完成，请先完成更新后再开始运行。";
            UpdateDisplay();
            return;
        }

        _hudStatusMessage = null;

        if (!_manualSelectionEnabled)
        {
            if (!TryApplyAutoLineAndServiceSelection(out var autoSelectionError))
            {
                _sessionRunning = false;
                _usingLiveMemory = false;
                StopLiveMemorySampling();
                _hudStatusMessage = autoSelectionError;
                UpdateDisplay();
                return;
            }
        }
        else if (_selectedService is null)
        {
            _sessionRunning = false;
            _usingLiveMemory = false;
            StopLiveMemorySampling();
            _hudStatusMessage = "未选择运行图：请在 Debug -> Manual Line Selection 中选择后再开始。";
            UpdateDisplay();
            return;
        }

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
        _lastScoredStationId = null;
        _lastDoorOpenTransitionAt = null;
        _activeApproachTargetStopDistance = null;
        _activeApproachScheduledSeconds = null;
        _activeApproachOvershootFaultTriggered = false;
        _activeRunningSegmentMapping = null;
        _mapLastTrainMarkerDistanceMeters = null;
        _latestApproachSnapshot = null;
        _latestApproachState = null;
        ResetApproachDisplaySmoothing();
        _isApproachPanelPinned = false;
        StopApproachValueAnimation();
        StopScoreCountupAnimation();
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

        // Initialize map if available (fire and forget)
        _ = InitializeMapAsync();

        UpdateDisplay();
    }

    private void EndSession()
    {
        if (_sessionRunning)
        {
            ExportSessionReport();
        }

        _sessionRunning = false;
        _hudStatusMessage = null;
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
        _activeRunningSegmentMapping = null;
        _mapLastTrainMarkerDistanceMeters = null;
        _latestApproachSnapshot = null;
        _latestApproachState = null;
        ResetApproachDisplaySmoothing();
        _isApproachPanelPinned = false;
        StopApproachValueAnimation();
        StopScoreCountupAnimation();
        StopApproachRealtimeUpdateTimer();
        HideApproachPanel(immediate: true);
        CloseMelodySelectionPanel();

        // Clear map
        _ = ClearMapAsync();

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

}
