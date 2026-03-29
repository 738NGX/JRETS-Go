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
        return _selectedService?.Train.Type ?? string.Empty;
    }

    private string GetDirectionText(TrainDisplayState state)
    {
        return _trainRouteService.GetDirectionText(_lineConfiguration, _selectedService?.Train, state);
    }

    private void RefreshUpcomingStations(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        _upcomingStations.Clear();

        var stopStations = _timelineService.BuildStopStations(_lineConfiguration, IsStopForSelectedService);

        if (stopStations.Count == 0)
        {
            return;
        }

        var timelineState = _timelineService.ComputeState(
            snapshot,
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

}
