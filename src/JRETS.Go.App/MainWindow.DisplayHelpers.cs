using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App;

public partial class MainWindow
{
    private double GetTotalScore()
    {
        return _driveReportWorkflowService.GetTotalScore(_stationScores);
    }

    private IReadOnlyDictionary<int, double>? TryLoadDebugStationDisplacementsMeters(LineConfiguration lineConfiguration)
    {
        try
        {
            if (lineConfiguration.MapInfo is null || string.IsNullOrWhiteSpace(lineConfiguration.MapInfo.Stations))
            {
                return null;
            }

            var filePath = Path.Combine(AppContext.BaseDirectory, "configs", "map", lineConfiguration.MapInfo.Stations);
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = File.ReadAllText(filePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, StationMapDataJson>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            if (dict is null || dict.Count == 0)
            {
                return null;
            }

            // Displacement in map config is kilometers; convert to meters for runtime snapshot.
            var result = new Dictionary<int, double>();
            foreach (var (stationIdText, value) in dict)
            {
                if (int.TryParse(stationIdText, out var stationId))
                {
                    result[stationId] = value.Displacement * 1000.0;
                }
            }

            return result.Count == 0 ? null : result;
        }
        catch
        {
            return null;
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
                DisplayName = $"{train.Type} ({train.Id})"
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
        LineColorPreview.Background = ResolveBrushForLineColor(_lineConfiguration.LineInfo.LineColor);
    }

    private void ApplyLineColor(bool doorOpen, StationInfo? displayStation, int? mapFromStationId, int? mapToStationId)
    {
        var color = doorOpen
            ? ResolveEffectiveStationLineColor(displayStation)
            : ResolveEffectiveSegmentLineColor(mapFromStationId, mapToStationId);
        LineColorPreview.Background = ResolveBrushForLineColor(color);
    }

    private SolidColorBrush ResolveBrushForLineColor(string? colorText)
    {
        if (!string.IsNullOrWhiteSpace(colorText))
        {
            try
            {
                var parsed = ColorConverter.ConvertFromString(colorText);
                if (parsed is Color c)
                {
                    return new SolidColorBrush(c);
                }
            }
            catch
            {
                // Fall back to the app default color.
            }
        }

        return new SolidColorBrush(Color.FromRgb(0, 178, 229));
    }

    private StationInfo? FindStationById(int stationId)
    {
        return _lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
    }

    private string ResolveEffectiveLineCode(StationInfo? station)
    {
        if (station is not null && station.LineCodeOverride is not null)
        {
            return station.LineCodeOverride.Trim();
        }

        return _lineConfiguration.LineInfo.Code;
    }

    private string ResolveEffectiveStationLineColor(StationInfo? station)
    {
        if (station is not null && station.LineColorOverride is not null)
        {
            var overrideColor = station.LineColorOverride.Trim();
            if (!string.IsNullOrWhiteSpace(overrideColor))
            {
                return overrideColor;
            }
        }

        return _lineConfiguration.LineInfo.LineColor;
    }

    private string ResolveEffectiveSegmentLineColor(int? fromStationId, int? toStationId)
    {
        if (fromStationId.HasValue)
        {
            var fromStation = FindStationById(fromStationId.Value);
            if (fromStation is not null && !string.IsNullOrWhiteSpace(fromStation.LineColorOverride))
            {
                return fromStation.LineColorOverride.Trim();
            }
        }

        if (toStationId.HasValue)
        {
            var toStation = FindStationById(toStationId.Value);
            if (toStation is not null && !string.IsNullOrWhiteSpace(toStation.LineColorOverride))
            {
                return toStation.LineColorOverride.Trim();
            }
        }

        return _lineConfiguration.LineInfo.LineColor;
    }

    private string GetServiceTypeDisplayName()
    {
        return _selectedService?.Train.Type ?? string.Empty;
    }

    private string GetDirectionText(TrainDisplayState state)
    {
        return _trainRouteService.GetDirectionText(_lineConfiguration, _selectedService?.Train, state);
    }

    private StationContext BuildStationContext(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        var currentStop = state.CurrentStopStation;
        var nextStop = state.NextStation;
        var timelineAnchorStationId = currentStop?.Id ?? snapshot.NextStationId;

        var mapFromStationId = snapshot.DoorOpen
            ? (currentStop?.Id ?? _lastKnownStopStationId ?? timelineAnchorStationId)
            : (_activeRunningSegmentMapping?.FromStationId ?? _lastKnownStopStationId ?? snapshot.NextStationId);
        var mapToStationId = snapshot.DoorOpen
            ? (nextStop?.Id ?? mapFromStationId)
            : (_activeRunningSegmentMapping?.ToStationId ?? nextStop?.Id ?? mapFromStationId);

        StationInfo? hudDisplayStation;
        string hudStatusText;
        if (snapshot.DoorOpen)
        {
            hudDisplayStation = currentStop;
            hudStatusText = "ただいま";
        }
        else
        {
            hudDisplayStation = nextStop;
            var remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;
            hudStatusText = remainingDistance < 400 ? "まもなく" : "次は";
        }

        return new StationContext
        {
            CurrentStopStation = currentStop,
            NextStopStation = nextStop,
            TimelineAnchorStationId = timelineAnchorStationId,
            MapFromStationId = mapFromStationId,
            MapToStationId = mapToStationId,
            HudDisplayStation = hudDisplayStation,
            HudStatusText = hudStatusText,
            Approach = new ApproachContext
            {
                TargetStopDistanceMeters = _activeApproachTargetStopDistance,
                ScheduledSeconds = _activeApproachScheduledSeconds,
                OvershootFaultTriggered = _activeApproachOvershootFaultTriggered
            }
        };
    }

    private void RefreshUpcomingStations(RealtimeSnapshot snapshot, TrainDisplayState state, StationContext stationContext)
    {
        _upcomingStations.Clear();

        var stopStations = _timelineService.BuildStopStations(_lineConfiguration, IsStopForSelectedService);

        if (stopStations.Count == 0)
        {
            return;
        }

        var timelineSnapshot = new RealtimeSnapshot
        {
            CapturedAt = snapshot.CapturedAt,
            NextStationId = stationContext.TimelineAnchorStationId,
            DoorOpen = snapshot.DoorOpen,
            MainClockSeconds = snapshot.MainClockSeconds,
            TimetableHour = snapshot.TimetableHour,
            TimetableMinute = snapshot.TimetableMinute,
            TimetableSecond = snapshot.TimetableSecond,
            CurrentDistanceMeters = snapshot.CurrentDistanceMeters,
            TargetStopDistanceMeters = snapshot.TargetStopDistanceMeters,
            LinePath = snapshot.LinePath
        };

        var timelineState = _timelineService.ComputeState(
            _lineConfiguration,
            timelineSnapshot,
            stopStations,
            TimelineVisibleTokenCount,
            _timelineInitialized,
            _timelineActiveToken,
            _timelineWindowStart);
        _timelineInitialized = timelineState.Initialized;
        _timelineActiveToken = timelineState.ActiveToken;
        _timelineWindowStart = timelineState.WindowStart;
        _timelineLastDoorOpen = timelineState.LastDoorOpen;
        var totalTokens = timelineState.TotalTokens;

        var finishedBrush = new SolidColorBrush(Color.FromRgb(120, 120, 120));
        var activeBrush = IsActiveTokenBlinkRed(snapshot.CapturedAt)
            ? new SolidColorBrush(Color.FromRgb(190, 24, 24))
            : finishedBrush;


        var isLoopLine = _lineConfiguration.LineInfo.IsLoop;

        // For loop lines, allow showing beyond the theoretical end to display closure
        var endExclusive = isLoopLine
            ? _timelineWindowStart + TimelineVisibleTokenCount
            : Math.Min(totalTokens, _timelineWindowStart + TimelineVisibleTokenCount);

        for (var tokenIndex = _timelineWindowStart; tokenIndex < endExclusive; tokenIndex++)
        {
            var isStation = tokenIndex % 2 == 0;
            var stationIndex = tokenIndex / 2;
            var isFirstVisibleStation = isStation && stationIndex == _timelineWindowStart / 2;
            
            // For loop lines, use modulo to wrap around; for linear lines, clamp to bounds
            int resolvedStationIndex;
            if (isLoopLine && isStation)
            {
                resolvedStationIndex = stationIndex % stopStations.Count;
            }
            else
            {
                resolvedStationIndex = Math.Clamp(stationIndex, 0, stopStations.Count - 1);
            }
            
            var station = stopStations[resolvedStationIndex];

            var fill = ResolveUpcomingArrowFill(stopStations, tokenIndex, stationIndex, isStation, isLoopLine, finishedBrush, activeBrush);

            _upcomingStations.Add(new UpcomingStationItem
            {
                NameLabel = isStation ? station.NameJp : string.Empty,
                CodeLabel = isStation
                    ? string.IsNullOrWhiteSpace(ResolveEffectiveLineCode(station)) || station.Number <= 0
                        ? string.Empty
                        : $"{ResolveEffectiveLineCode(station)}-{station.Number:D2}"
                    : string.Empty,
                ArrowFill = fill,
                ArrowGeometry = BuildArrowGeometry(isFirstVisibleStation, isStation),
                ArrowWidth = isStation ? StationArrowWidth : SegmentArrowWidth,
                StationMarkerVisibility = isStation ? Visibility.Visible : Visibility.Collapsed
            });
        }
    }

    private Brush ResolveUpcomingArrowFill(
        IReadOnlyList<StationInfo> stopStations,
        int tokenIndex,
        int stationIndex,
        bool isStation,
        bool isLoopLine,
        Brush finishedBrush,
        Brush activeBrush)
    {
        if (stopStations.Count == 0)
        {
            return GetFutureBrush();
        }

        if (tokenIndex < _timelineActiveToken)
        {
            return finishedBrush;
        }

        if (tokenIndex == _timelineActiveToken)
        {
            return activeBrush;
        }

        var resolvedStationIndex = stationIndex;
        if (isLoopLine)
        {
            resolvedStationIndex %= stopStations.Count;
        }
        else
        {
            resolvedStationIndex = Math.Clamp(resolvedStationIndex, 0, stopStations.Count - 1);
        }

        var station = stopStations[resolvedStationIndex];
        return ResolveBrushForLineColor(ResolveEffectiveStationLineColor(station));
    }

    private static bool IsActiveTokenBlinkRed(DateTime capturedAt)
    {
        const long halfPeriodMilliseconds = 1000;
        var ticksMs = capturedAt.ToUniversalTime().Ticks / TimeSpan.TicksPerMillisecond;
        return (ticksMs / halfPeriodMilliseconds) % 2 == 0;
    }

    private void UpdateLatchedStationId(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (snapshot.DoorOpen)
        {
            _latchedStationId = state.CurrentStopStation?.Id ?? _latchedStationId;
            return;
        }

        _latchedStationId = state.NextStation?.Id ?? _latchedStationId;
    }

    private sealed class StationContext
    {
        public required StationInfo? CurrentStopStation { get; init; }

        public required StationInfo? NextStopStation { get; init; }

        public required int TimelineAnchorStationId { get; init; }

        public required int MapFromStationId { get; init; }

        public required int MapToStationId { get; init; }

        public required StationInfo? HudDisplayStation { get; init; }

        public required string HudStatusText { get; init; }

        public required ApproachContext Approach { get; init; }
    }

    private sealed class ApproachContext
    {
        public required double? TargetStopDistanceMeters { get; init; }

        public required int? ScheduledSeconds { get; init; }

        public required bool OvershootFaultTriggered { get; init; }
    }

}
