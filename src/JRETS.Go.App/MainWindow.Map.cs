using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App;

public partial class MainWindow
{
    // ==================== Map Related ====================

    private async Task InitializeMapAsync()
    {
        try
        {
            _miniMapDataAvailable = false;
            if (_lineConfiguration?.MapInfo == null)
            {
                _currentMapStations = null;
                _currentMapRoute = null;
                SetMapStatus("MAP: OFF");
                if (_isMiniMapPanelVisible)
                {
                    DrawMapAvailabilityMessage("この路線の地図データがありません", "No map data for this line");
                }
                return;
            }

            // Load map data from configs/map
            string mapDir = Path.Combine("configs", "map");
            string stationsPath = Path.Combine(mapDir, _lineConfiguration.MapInfo.Stations);
            string routePath = Path.Combine(mapDir, _lineConfiguration.MapInfo.Route);

            _currentMapStations = await LoadStationMapDataAsync(stationsPath);
            _currentMapRoute = await LoadRouteDataAsync(routePath);
            _miniMapDataAvailable = _currentMapStations is not null && _currentMapRoute is not null;

            if (_miniMapDataAvailable)
            {
                if (_sessionRunning)
                {
                    AnimateMiniMapPanel(show: true);
                    var snapshot = GetCurrentSnapshot();
                    var state = _displayStateResolver.Resolve(_lineConfiguration, snapshot);
                    _latestApproachSnapshot = snapshot;
                    _latestApproachState = state;
                    await RenderMapAsync(snapshot, state, force: true);
                }
                else if (_isMiniMapPanelVisible)
                {
                    DrawMapAvailabilityMessage("運転外です", "Not driving now");
                }
            }
            else
            {
                _lastDataSourceError = "Map data load failed: station/route json not found or invalid.";
                SetMapStatus("MAP: DATA ERR");
                if (_isMiniMapPanelVisible)
                {
                    DrawMapAvailabilityMessage("この路線の地図データがありません", "No map data for this line");
                }
            }
        }
        catch
        {
            _miniMapDataAvailable = false;
            _currentMapStations = null;
            _currentMapRoute = null;
            _lastDataSourceError = "Map initialization failed.";
            SetMapStatus("MAP: INIT ERR");
            if (_isMiniMapPanelVisible)
            {
                DrawMapAvailabilityMessage("地図の初期化に失敗しました", "Failed to initialize map");
            }
        }
    }

    private async Task<StationMapData[]?> LoadStationMapDataAsync(string filePath)
    {
        try
        {
            var fullPath = Path.Combine(AppContext.BaseDirectory, filePath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(fullPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, StationMapDataJson>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            if (dict == null) return null;

            return dict.Select(x => new StationMapData
            {
                Id = int.TryParse(x.Key, out var parsedId) ? parsedId : x.Value.Id,
                Displacement = x.Value.Displacement,
                Coordinates = x.Value.Coordinates,
                Labeled = x.Value.Labeled
            })
            .OrderBy(x => x.Displacement)
            .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private async Task<RoutePathData?> LoadRouteDataAsync(string filePath)
    {
        try
        {
            var fullPath = Path.Combine(AppContext.BaseDirectory, filePath);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            string json = await File.ReadAllTextAsync(fullPath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, double[]>>(json);

            if (dict == null) return null;

            var routeData = new RoutePathData
            {
                RoutePoints = dict
                    .Select(x => new
                    {
                        Distance = double.Parse(x.Key, CultureInfo.InvariantCulture),
                        Coordinates = x.Value
                    })
                    .OrderBy(x => x.Distance)
                    .ToDictionary(x => x.Distance, x => x.Coordinates)
            };

            return routeData;
        }
        catch
        {
            return null;
        }
    }

    private async Task RenderMapAsync()
    {
        await RenderMapAsync(null, null, force: true);
    }

    private async Task RenderMapAsync(RealtimeSnapshot? snapshot, TrainDisplayState? state, bool force)
    {
        if (_currentMapStations == null || _currentMapRoute == null)
        {
            return;
        }

        try
        {
            var routeCoords = _currentMapRoute.RoutePoints
                .OrderBy(x => x.Key)
                .Select(x => new RoutePointEntry { DistanceKm = x.Key, Coordinates = x.Value })
                .ToArray();

            var fullLineCoords = routeCoords
                .Select(x => x.Coordinates)
                .Where(x => x.Length >= 2)
                .ToArray();

            // For loop lines, append the first point at the end to close the path visually
            if (_lineConfiguration.LineInfo.IsLoop && fullLineCoords.Length > 0)
            {
                fullLineCoords = [..fullLineCoords, fullLineCoords[0]];
            }

            var stationCoords = _currentMapStations
                .Where(s => s.Coordinates.Length >= 2)
                .Select(s => s.Coordinates)
                .ToArray();

            var mapStationsById = _currentMapStations.ToDictionary(x => x.Id, x => x);
            if (snapshot is not null && state is not null && !snapshot.DoorOpen)
            {
                EnsureRunningSegmentMapping(snapshot, state);
            }

            var runningFocus = false;
            StationMapData? runningFrom = null;
            StationMapData? runningTo = null;
            int? highlightedStopStationId = null;

            if (_sessionRunning && snapshot is not null && state is not null)
            {
                if (snapshot.DoorOpen && state.CurrentStopStation is not null)
                {
                    highlightedStopStationId = state.CurrentStopStation.Id;
                }
                else if (!snapshot.DoorOpen && state.NextStation is not null)
                {
                    if (mapStationsById.TryGetValue(snapshot.NextStationId, out var fromStation)
                        && mapStationsById.TryGetValue(state.NextStation.Id, out var toStation)
                        && fromStation.Id != toStation.Id)
                    {
                        runningFocus = true;
                        runningFrom = fromStation;
                        runningTo = toStation;
                    }
                }
            }

            double[][] displayRouteCoords;
            MiniMapViewport viewport;

            if (runningFocus && runningFrom is not null && runningTo is not null)
            {
                var minLng = Math.Min(runningFrom.Coordinates[0], runningTo.Coordinates[0]);
                var maxLng = Math.Max(runningFrom.Coordinates[0], runningTo.Coordinates[0]);
                var minLat = Math.Min(runningFrom.Coordinates[1], runningTo.Coordinates[1]);
                var maxLat = Math.Max(runningFrom.Coordinates[1], runningTo.Coordinates[1]);
                const double focusMarginDegrees = 0.018;

                var focusCoords = fullLineCoords
                    .Where(c =>
                        c[0] >= minLng - focusMarginDegrees && c[0] <= maxLng + focusMarginDegrees
                        && c[1] >= minLat - focusMarginDegrees && c[1] <= maxLat + focusMarginDegrees)
                    .ToArray();

                if (focusCoords.Length < 2)
                {
                    focusCoords =
                    [
                        runningFrom.Coordinates,
                        runningTo.Coordinates
                    ];
                }

                var width = MiniMapCanvas.ActualWidth > 1 ? MiniMapCanvas.ActualWidth : 400;
                var height = MiniMapCanvas.ActualHeight > 1 ? MiniMapCanvas.ActualHeight : 300;
                const double padding = 18;
                var centerLng = (runningFrom.Coordinates[0] + runningTo.Coordinates[0]) / 2.0;
                var centerLat = (runningFrom.Coordinates[1] + runningTo.Coordinates[1]) / 2.0;
                viewport = BuildFocusedMiniMapViewport(focusCoords, width, height, padding, centerLng, centerLat, 0.5);
                displayRouteCoords = fullLineCoords.Length >= 2 ? fullLineCoords : stationCoords;
            }
            else
            {
                displayRouteCoords = fullLineCoords.Length >= 2 ? fullLineCoords : stationCoords;

                var allCoords = routeCoords
                    .Select(x => x.Coordinates)
                    .Concat(stationCoords)
                    .Where(c => c.Length >= 2)
                    .ToArray();

                if (allCoords.Length == 0)
                {
                    SetMapStatus("MAP: EMPTY");
                    ClearNativeMap();
                    return;
                }

                var width = MiniMapCanvas.ActualWidth > 1 ? MiniMapCanvas.ActualWidth : 400;
                var height = MiniMapCanvas.ActualHeight > 1 ? MiniMapCanvas.ActualHeight : 300;
                const double padding = 12;
                viewport = BuildMiniMapViewport(allCoords, width, height, padding);
            }

            if (displayRouteCoords.Length == 0)
            {
                SetMapStatus("MAP: EMPTY");
                ClearNativeMap();
                return;
            }

            ClearNativeMap();

            if (_nativeMapBaseLayerVisible)
            {
                var tileCount = await DrawOsmTilesAsync(viewport);
                if (tileCount == 0)
                {
                    DrawNativeMapGrid(viewport.Width, viewport.Height);
                }
            }

            var lineColor = (Color)ColorConverter.ConvertFromString(_lineConfiguration?.LineInfo.LineColor ?? "#00B2E5");
            var lineBrush = new SolidColorBrush(lineColor);

            if (displayRouteCoords.Length > 1)
            {
                var projectedRoute = new List<Point>(displayRouteCoords.Length);
                foreach (var coord in displayRouteCoords)
                {
                    if (coord.Length < 2)
                    {
                        continue;
                    }

                    projectedRoute.Add(ProjectToMiniMap(coord[0], coord[1], viewport));
                }

                var sanitizedRoute = SanitizeRoutePoints(projectedRoute);
                var smoothedRoute = SmoothPolylineChaikin(sanitizedRoute, iterations: 2);

                var polyline = new System.Windows.Shapes.Polyline
                {
                    Stroke = lineBrush,
                    StrokeThickness = 3,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = 0.9
                };

                foreach (var p in smoothedRoute)
                {
                    polyline.Points.Add(p);
                }

                MiniMapCanvas.Children.Add(polyline);
            }

            if (!snapshot?.DoorOpen ?? false)
            {
                var mappedTrainDistanceMeters = TryMapGameDistanceToConfigDistance(snapshot!.CurrentDistanceMeters, out var value)
                    ? value
                    : (double?)null;
                if (mappedTrainDistanceMeters.HasValue
                    && TryInterpolateRouteCoordinate(routeCoords, mappedTrainDistanceMeters.Value / 1000.0, out var trainCoord))
                {
                    var trainPoint = ProjectToMiniMap(trainCoord[0], trainCoord[1], viewport);
                    var trainDot = new System.Windows.Shapes.Ellipse
                    {
                        Width = 13,
                        Height = 13,
                        Fill = Brushes.Lime,
                        Stroke = Brushes.Black,
                        StrokeThickness = 2.0
                    };

                    Canvas.SetLeft(trainDot, trainPoint.X - trainDot.Width / 2);
                    Canvas.SetTop(trainDot, trainPoint.Y - trainDot.Height / 2);
                    MiniMapCanvas.Children.Add(trainDot);
                }
            }

            var stationNameById = _lineConfiguration!.Stations
                .GroupBy(x => x.Id)
                .ToDictionary(
                    x => x.Key,
                    x => x.FirstOrDefault()?.NameJp ?? x.Key.ToString(CultureInfo.InvariantCulture));

            var stationRenderEntries = new List<(StationMapData Station, Point Point, bool IsVisible)>();

            foreach (var station in _currentMapStations!)
            {
                if (station.Coordinates.Length < 2)
                {
                    continue;
                }

                var p = ProjectToMiniMap(station.Coordinates[0], station.Coordinates[1], viewport);
                var isVisible = p.X >= -8 && p.X <= viewport.Width + 8 && p.Y >= -8 && p.Y <= viewport.Height + 8;
                stationRenderEntries.Add((station, p, isVisible));

                var isHighlighted = highlightedStopStationId.HasValue && station.Id == highlightedStopStationId.Value;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = isHighlighted ? 11 : 7,
                    Height = isHighlighted ? 11 : 7,
                    Fill = isHighlighted ? Brushes.Red : lineBrush,
                    Stroke = Brushes.White,
                    StrokeThickness = isHighlighted ? 2.0 : 1.5
                };

                Canvas.SetLeft(dot, p.X - dot.Width / 2);
                Canvas.SetTop(dot, p.Y - dot.Height / 2);
                MiniMapCanvas.Children.Add(dot);
            }

            if (runningFocus)
            {
                foreach (var entry in stationRenderEntries.Where(x => x.IsVisible))
                {
                    var stationName = stationNameById.TryGetValue(entry.Station.Id, out var name)
                        ? name
                        : entry.Station.Id.ToString(CultureInfo.InvariantCulture);

                    var label = new TextBlock
                    {
                        Text = stationName,
                        Foreground = Brushes.White,
                        Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Padding = new Thickness(4, 1, 4, 1)
                    };
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = label.DesiredSize;

                    var labelLeft = Math.Clamp(entry.Point.X + 8, 0, Math.Max(0, viewport.Width - size.Width));
                    var labelTop = Math.Clamp(entry.Point.Y - size.Height / 2, 0, Math.Max(0, viewport.Height - size.Height));

                    Canvas.SetLeft(label, labelLeft);
                    Canvas.SetTop(label, labelTop);
                    MiniMapCanvas.Children.Add(label);
                }
            }
            else
            {
                var currentStationId = highlightedStopStationId
                    ?? state?.CurrentStopStation?.Id
                    ?? snapshot?.NextStationId;

                var renderLookup = stationRenderEntries.ToDictionary(x => x.Station.Id, x => x);

                // Render current station with bold style
                if (currentStationId.HasValue
                    && renderLookup.TryGetValue(currentStationId.Value, out var currentEntry)
                    && currentEntry.IsVisible)
                {
                    var currentName = stationNameById.TryGetValue(currentStationId.Value, out var name)
                        ? name
                        : currentStationId.Value.ToString(CultureInfo.InvariantCulture);

                    var currentLabel = new TextBlock
                    {
                        Text = currentName,
                        Foreground = Brushes.White,
                        Background = new SolidColorBrush(Color.FromArgb(176, 0, 0, 0)),
                        FontSize = 12,
                        FontWeight = FontWeights.Bold,
                        Padding = new Thickness(4, 1, 4, 1)
                    };
                    currentLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = currentLabel.DesiredSize;
                    var labelLeft = Math.Clamp(currentEntry.Point.X + 8, 0, Math.Max(0, viewport.Width - size.Width));
                    var labelTop = Math.Clamp(currentEntry.Point.Y - size.Height / 2, 0, Math.Max(0, viewport.Height - size.Height));

                    Canvas.SetLeft(currentLabel, labelLeft);
                    Canvas.SetTop(currentLabel, labelTop);
                    MiniMapCanvas.Children.Add(currentLabel);
                }

                // Render labeled stations on the right side
                foreach (var entry in stationRenderEntries.Where(x => x.IsVisible && x.Station.Labeled && x.Station.Id != currentStationId))
                {
                    var stationName = stationNameById.TryGetValue(entry.Station.Id, out var name)
                        ? name
                        : entry.Station.Id.ToString(CultureInfo.InvariantCulture);

                    var label = new TextBlock
                    {
                        Text = stationName,
                        Foreground = Brushes.White,
                        Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)),
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Padding = new Thickness(4, 1, 4, 1)
                    };
                    label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var size = label.DesiredSize;
                    var labelLeft = Math.Clamp(entry.Point.X + 8, 0, Math.Max(0, viewport.Width - size.Width));
                    var labelTop = Math.Clamp(entry.Point.Y - size.Height / 2, 0, Math.Max(0, viewport.Height - size.Height));

                    Canvas.SetLeft(label, labelLeft);
                    Canvas.SetTop(label, labelTop);
                    MiniMapCanvas.Children.Add(label);
                }
            }

            var baseState = _nativeMapBaseLayerVisible ? "DARK ON" : "BASE OFF";
            var mode = runningFocus ? "RUN FOCUS" : "FULL";
            SetMapStatus($"MAP: NATIVE | {baseState} | {mode} | ROUTE OK | STN OK");
        }
        catch
        {
            _lastDataSourceError = "Map render failed: native draw error.";
            SetMapStatus("MAP: RENDER ERR");
            if (_isMiniMapPanelVisible)
            {
                DrawMapAvailabilityMessage("地図の描画に失敗しました", "Map render failed");
            }
        }
    }

    private async Task ToggleMapBaseLayerAsync()
    {
        _nativeMapBaseLayerVisible = !_nativeMapBaseLayerVisible;
        _mapLastDoorOpen = null;
        _mapLastCurrentStationId = -1;
        _mapLastNextStationId = -1;
        _mapLastStopStationId = -1;
        _mapLastTrainMarkerDistanceMeters = null;

        if (!_miniMapDataAvailable)
        {
            if (_isMiniMapPanelVisible)
            {
                DrawMapAvailabilityMessage("この路線の地図データがありません", "No map data for this line");
            }

            return;
        }

        if (_isMiniMapPanelVisible)
        {
            await RenderMapAsync(_latestApproachSnapshot, _latestApproachState, force: true);
        }
    }

    private async Task ClearMapAsync()
    {
        ClearNativeMap();
        _mapLastDoorOpen = null;
        _mapLastCurrentStationId = -1;
        _mapLastNextStationId = -1;
        _mapLastStopStationId = -1;
        _mapLastTrainMarkerDistanceMeters = null;
        SetMapStatus("MAP: CLEARED");
        AnimateMiniMapPanel(show: false);
        await Task.CompletedTask;
    }

    private void RequestMapRender(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (_currentMapStations is null || _currentMapRoute is null)
        {
            return;
        }

        EnsureRunningSegmentMapping(snapshot, state);

        var currentId = snapshot.NextStationId;
        var nextId = state.NextStation?.Id ?? -1;
        var stopId = state.CurrentStopStation?.Id ?? -1;
        var mappedTrainDistanceMeters = TryMapGameDistanceToConfigDistance(snapshot.CurrentDistanceMeters, out var mappedDistance)
            ? mappedDistance
            : (double?)null;
        var trainMarkerUnchanged = !mappedTrainDistanceMeters.HasValue
            || (_mapLastTrainMarkerDistanceMeters.HasValue
                && Math.Abs(mappedTrainDistanceMeters.Value - _mapLastTrainMarkerDistanceMeters.Value) < 8.0);

        if (_mapLastDoorOpen == snapshot.DoorOpen
            && _mapLastCurrentStationId == currentId
            && _mapLastNextStationId == nextId
            && _mapLastStopStationId == stopId
            && trainMarkerUnchanged)
        {
            return;
        }

        _mapLastDoorOpen = snapshot.DoorOpen;
        _mapLastCurrentStationId = currentId;
        _mapLastNextStationId = nextId;
        _mapLastStopStationId = stopId;
        _mapLastTrainMarkerDistanceMeters = mappedTrainDistanceMeters;

        _ = RenderMapAsync(snapshot, state, force: false);
    }

    private void EnsureRunningSegmentMapping(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (snapshot.DoorOpen)
        {
            _activeRunningSegmentMapping = null;
            return;
        }

        if (_activeRunningSegmentMapping is not null)
        {
            return;
        }

        var departureStationId = state.CurrentStopStation?.Id ?? _lastKnownStopStationId;
        var nextStopStationId = _activeApproachStationId ?? state.NextStation?.Id;
        TryBuildRunningSegmentMapping(snapshot, departureStationId, nextStopStationId);
    }

    private void TryBuildRunningSegmentMapping(RealtimeSnapshot snapshot, int? departureStationId, int? nextStopStationId)
    {
        if (!departureStationId.HasValue || !nextStopStationId.HasValue)
        {
            return;
        }

        if (_activeApproachTargetStopDistance is null)
        {
            return;
        }

        if (!TryGetStationConfigDistanceMeters(departureStationId.Value, out var configFromMeters)
            || !TryGetStationConfigDistanceMeters(nextStopStationId.Value, out var configToMeters))
        {
            return;
        }

        var loopLengthMeters = TryGetLoopRouteLengthMeters(out var routeLengthMeters)
            ? routeLengthMeters
            : 0;

        var shouldWrapLoopDistance = _lineConfiguration?.LineInfo.IsLoop == true && loopLengthMeters > 1;
        var adjustedConfigToMeters = shouldWrapLoopDistance
            ? ChooseLoopAdjustedTargetDistance(configFromMeters, configToMeters, snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters, loopLengthMeters)
            : configToMeters;

        var gameFromMeters = snapshot.CurrentDistanceMeters;
        var gameToMeters = _activeApproachTargetStopDistance.Value;
        var gameDelta = gameToMeters - gameFromMeters;
        if (Math.Abs(gameDelta) < 0.001)
        {
            return;
        }

        var scale = (adjustedConfigToMeters - configFromMeters) / gameDelta;
        var offset = configFromMeters - scale * gameFromMeters;
        _activeRunningSegmentMapping = new RunningSegmentMapping
        {
            FromStationId = departureStationId.Value,
            ToStationId = nextStopStationId.Value,
            ConfigFromMeters = configFromMeters,
            ConfigToMeters = adjustedConfigToMeters,
            LoopLengthMeters = shouldWrapLoopDistance ? loopLengthMeters : 0,
            Scale = scale,
            Offset = offset
        };
    }

    private bool TryMapGameDistanceToConfigDistance(double gameDistanceMeters, out double mappedConfigMeters)
    {
        mappedConfigMeters = 0;
        if (_activeRunningSegmentMapping is null)
        {
            return false;
        }

        var raw = _activeRunningSegmentMapping.Offset + _activeRunningSegmentMapping.Scale * gameDistanceMeters;
        var min = Math.Min(_activeRunningSegmentMapping.ConfigFromMeters, _activeRunningSegmentMapping.ConfigToMeters);
        var max = Math.Max(_activeRunningSegmentMapping.ConfigFromMeters, _activeRunningSegmentMapping.ConfigToMeters);
        var clamped = Math.Clamp(raw, min, max);
        mappedConfigMeters = _activeRunningSegmentMapping.LoopLengthMeters > 1
            ? NormalizeLoopDistanceMeters(clamped, _activeRunningSegmentMapping.LoopLengthMeters)
            : clamped;
        return true;
    }

    private bool TryGetLoopRouteLengthMeters(out double loopLengthMeters)
    {
        loopLengthMeters = 0;

        if (_currentMapRoute?.RoutePoints is { Count: > 0 })
        {
            loopLengthMeters = _currentMapRoute.RoutePoints.Keys.Max() * 1000.0;
            if (loopLengthMeters > 1)
            {
                return true;
            }
        }

        if (_currentMapStations is { Length: > 0 })
        {
            loopLengthMeters = _currentMapStations.Max(x => x.Displacement) * 1000.0;
            return loopLengthMeters > 1;
        }

        return false;
    }

    private static double ChooseLoopAdjustedTargetDistance(double fromMeters, double targetMeters, double gameDelta, double loopLengthMeters)
    {
        var bestTarget = targetMeters;
        var bestScore = double.PositiveInfinity;
        var desiredSign = Math.Sign(gameDelta);

        for (var shift = -1; shift <= 1; shift++)
        {
            var candidate = targetMeters + shift * loopLengthMeters;
            var candidateDelta = candidate - fromMeters;

            // Prefer candidates with matching direction and shortest segment on the unwrapped axis.
            var signPenalty = desiredSign != 0 && Math.Sign(candidateDelta) != desiredSign ? 1_000_000.0 : 0.0;
            var score = Math.Abs(candidateDelta) + signPenalty;
            if (score < bestScore)
            {
                bestScore = score;
                bestTarget = candidate;
            }
        }

        return bestTarget;
    }

    private static double NormalizeLoopDistanceMeters(double distanceMeters, double loopLengthMeters)
    {
        var normalized = distanceMeters % loopLengthMeters;
        if (normalized < 0)
        {
            normalized += loopLengthMeters;
        }

        return normalized;
    }

    private bool TryGetStationConfigDistanceMeters(int stationId, out double distanceMeters)
    {
        distanceMeters = 0;
        if (_currentMapStations is null)
        {
            return false;
        }

        var station = _currentMapStations.FirstOrDefault(x => x.Id == stationId);
        if (station is null)
        {
            return false;
        }

        distanceMeters = station.Displacement * 1000.0;
        return true;
    }

    private static bool TryInterpolateRouteCoordinate(IReadOnlyList<RoutePointEntry> routeCoords, double mappedDistanceKm, out double[] coordinate)
    {
        coordinate = [];
        if (routeCoords.Count == 0)
        {
            return false;
        }

        if (routeCoords.Count == 1 || mappedDistanceKm <= routeCoords[0].DistanceKm)
        {
            var c0 = routeCoords[0].Coordinates;
            if (c0.Length < 2)
            {
                return false;
            }

            coordinate = [c0[0], c0[1]];
            return true;
        }

        for (var i = 1; i < routeCoords.Count; i++)
        {
            var prev = routeCoords[i - 1];
            var next = routeCoords[i];
            if (prev.Coordinates.Length < 2 || next.Coordinates.Length < 2)
            {
                continue;
            }

            if (mappedDistanceKm > next.DistanceKm)
            {
                continue;
            }

            var segmentLengthKm = next.DistanceKm - prev.DistanceKm;
            var t = Math.Abs(segmentLengthKm) < 1e-9 ? 0 : (mappedDistanceKm - prev.DistanceKm) / segmentLengthKm;
            t = Math.Clamp(t, 0, 1);
            coordinate =
            [
                prev.Coordinates[0] + (next.Coordinates[0] - prev.Coordinates[0]) * t,
                prev.Coordinates[1] + (next.Coordinates[1] - prev.Coordinates[1]) * t
            ];
            return true;
        }

        var tail = routeCoords[^1].Coordinates;
        if (tail.Length < 2)
        {
            return false;
        }

        coordinate = [tail[0], tail[1]];
        return true;
    }

    private void SetMapStatus(string status)
    {
        if (MapStatusTextBlock is not null)
        {
            // For now, we are not showing map status to avoid confusion for users. The status is still updated internally for debugging purposes and may be shown in the future if needed.
            // MapStatusTextBlock.Text = status;
        }
    }

    private void ClearNativeMap()
    {
        MiniMapCanvas.Children.Clear();
    }

    private void DrawNativeMapGrid(double width, double height)
    {
        var gridBrush = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        const double step = 32;

        for (var x = 0.0; x <= width; x += step)
        {
            var v = new System.Windows.Shapes.Line
            {
                X1 = x,
                Y1 = 0,
                X2 = x,
                Y2 = height,
                Stroke = gridBrush,
                StrokeThickness = 1
            };
            MiniMapCanvas.Children.Add(v);
        }

        for (var y = 0.0; y <= height; y += step)
        {
            var h = new System.Windows.Shapes.Line
            {
                X1 = 0,
                Y1 = y,
                X2 = width,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 1
            };
            MiniMapCanvas.Children.Add(h);
        }
    }

    private async Task<int> DrawOsmTilesAsync(MiniMapViewport viewport)
    {
        var tileMinX = Math.Max(0, (int)Math.Floor(viewport.MinWorldX / 256.0) - 1);
        var tileMaxX = Math.Min((1 << viewport.Zoom) - 1, (int)Math.Floor(viewport.MaxWorldX / 256.0) + 1);
        var tileMinY = Math.Max(0, (int)Math.Floor(viewport.MinWorldY / 256.0) - 1);
        var tileMaxY = Math.Min((1 << viewport.Zoom) - 1, (int)Math.Floor(viewport.MaxWorldY / 256.0) + 1);

        var requests = new List<(int X, int Y)>();
        for (var ty = tileMinY; ty <= tileMaxY; ty++)
        {
            for (var tx = tileMinX; tx <= tileMaxX; tx++)
            {
                requests.Add((tx, ty));
            }
        }

        var fetchTasks = requests.Select(async req => (req, image: await GetOsmTileAsync(viewport.Zoom, req.X, req.Y))).ToArray();
        var fetched = await Task.WhenAll(fetchTasks);

        var count = 0;
        foreach (var item in fetched)
        {
            if (item.image is null)
            {
                continue;
            }

            var tileWorldX = item.req.X * 256.0;
            var tileWorldY = item.req.Y * 256.0;
            var screenX = viewport.OffsetX + (tileWorldX - viewport.MinWorldX) * viewport.Scale;
            var screenY = viewport.OffsetY + (tileWorldY - viewport.MinWorldY) * viewport.Scale;
            var screenSize = 256.0 * viewport.Scale;

            var imageControl = new Image
            {
                Source = item.image,
                Width = screenSize,
                Height = screenSize,
                Opacity = 1.0,
                Stretch = Stretch.Fill
            };

            Canvas.SetLeft(imageControl, screenX);
            Canvas.SetTop(imageControl, screenY);
            MiniMapCanvas.Children.Add(imageControl);
            count++;
        }

        return count;
    }

    private async Task<BitmapImage?> GetOsmTileAsync(int zoom, int tileX, int tileY)
    {
        var key = $"dark/{zoom}/{tileX}/{tileY}";
        if (_osmTileCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var subdomain = ((tileX + tileY) % 4) switch
            {
                0 => "a",
                1 => "b",
                2 => "c",
                _ => "d"
            };
            var url = $"https://{subdomain}.basemaps.cartocdn.com/dark_all/{zoom}/{tileX}/{tileY}.png";
            using var response = await _osmHttpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            _osmTileCache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static MiniMapViewport BuildMiniMapViewport(double[][] coords, double width, double height, double padding)
    {
        var minLng = coords.Min(c => c[0]);
        var maxLng = coords.Max(c => c[0]);
        var minLat = coords.Min(c => c[1]);
        var maxLat = coords.Max(c => c[1]);

        var lngPad = Math.Max((maxLng - minLng) * 0.08, 0.0001);
        var latPad = Math.Max((maxLat - minLat) * 0.08, 0.0001);
        minLng -= lngPad;
        maxLng += lngPad;
        minLat -= latPad;
        maxLat += latPad;

        var zoom = ChooseOsmZoom(minLng, maxLng, minLat, maxLat, width, height);

        var minWorldX = LonToWorldX(minLng, zoom);
        var maxWorldX = LonToWorldX(maxLng, zoom);
        var minWorldY = LatToWorldY(maxLat, zoom);
        var maxWorldY = LatToWorldY(minLat, zoom);

        var drawWidth = Math.Max(1, width - padding * 2);
        var drawHeight = Math.Max(1, height - padding * 2);
        var worldWidth = Math.Max(1e-9, maxWorldX - minWorldX);
        var worldHeight = Math.Max(1e-9, maxWorldY - minWorldY);
        var scale = Math.Min(drawWidth / worldWidth, drawHeight / worldHeight);
        var scaledWidth = worldWidth * scale;
        var scaledHeight = worldHeight * scale;
        var offsetX = padding + (drawWidth - scaledWidth) / 2;
        var offsetY = padding + (drawHeight - scaledHeight) / 2;

        return new MiniMapViewport
        {
            Width = width,
            Height = height,
            Zoom = zoom,
            MinWorldX = minWorldX,
            MaxWorldX = maxWorldX,
            MinWorldY = minWorldY,
            MaxWorldY = maxWorldY,
            Scale = scale,
            OffsetX = offsetX,
            OffsetY = offsetY
        };
    }

    private static MiniMapViewport BuildFocusedMiniMapViewport(
        double[][] coords,
        double width,
        double height,
        double padding,
        double centerLng,
        double centerLat,
        double focusScale)
    {
        var baseViewport = BuildMiniMapViewport(coords, width, height, padding);

        var drawWidth = Math.Max(1, width - padding * 2);
        var drawHeight = Math.Max(1, height - padding * 2);
        var worldWidth = Math.Max(1e-9, baseViewport.MaxWorldX - baseViewport.MinWorldX);
        var worldHeight = Math.Max(1e-9, baseViewport.MaxWorldY - baseViewport.MinWorldY);
        var centerWorldX = LonToWorldX(centerLng, baseViewport.Zoom);
        var centerWorldY = LatToWorldY(centerLat, baseViewport.Zoom);

        var halfW = worldWidth * Math.Max(0.45, focusScale) / 2.0;
        var halfH = worldHeight * Math.Max(0.45, focusScale) / 2.0;
        var aspect = drawWidth / drawHeight;
        if (halfW / halfH > aspect)
        {
            halfH = halfW / aspect;
        }
        else
        {
            halfW = halfH * aspect;
        }

        var minWorldX = centerWorldX - halfW;
        var maxWorldX = centerWorldX + halfW;
        var minWorldY = centerWorldY - halfH;
        var maxWorldY = centerWorldY + halfH;

        var scale = Math.Min(drawWidth / Math.Max(1e-9, maxWorldX - minWorldX), drawHeight / Math.Max(1e-9, maxWorldY - minWorldY));
        var scaledWidth = (maxWorldX - minWorldX) * scale;
        var scaledHeight = (maxWorldY - minWorldY) * scale;
        var offsetX = padding + (drawWidth - scaledWidth) / 2;
        var offsetY = padding + (drawHeight - scaledHeight) / 2;

        return new MiniMapViewport
        {
            Width = width,
            Height = height,
            Zoom = baseViewport.Zoom,
            MinWorldX = minWorldX,
            MaxWorldX = maxWorldX,
            MinWorldY = minWorldY,
            MaxWorldY = maxWorldY,
            Scale = scale,
            OffsetX = offsetX,
            OffsetY = offsetY
        };
    }

    private static int ChooseOsmZoom(double minLng, double maxLng, double minLat, double maxLat, double width, double height)
    {
        var lonSpan = Math.Max(1e-7, maxLng - minLng);
        var y1 = LatToMercatorNormalized(maxLat);
        var y2 = LatToMercatorNormalized(minLat);
        var latSpan = Math.Max(1e-7, Math.Abs(y2 - y1));

        var zoomX = Math.Log(Math.Max(1.0, width) * 360.0 / (256.0 * lonSpan), 2);
        var zoomY = Math.Log(Math.Max(1.0, height) / (256.0 * latSpan), 2);
        var zoom = (int)Math.Floor(Math.Min(zoomX, zoomY));
        return Math.Clamp(zoom, 5, 15);
    }

    private static Point ProjectToMiniMap(double lng, double lat, MiniMapViewport viewport)
    {
        var worldX = LonToWorldX(lng, viewport.Zoom);
        var worldY = LatToWorldY(lat, viewport.Zoom);
        var x = viewport.OffsetX + (worldX - viewport.MinWorldX) * viewport.Scale;
        var y = viewport.OffsetY + (worldY - viewport.MinWorldY) * viewport.Scale;
        return new Point(x, y);
    }

    private IReadOnlyList<Point> SanitizeRoutePoints(IReadOnlyList<Point> input)
    {
        if (input.Count <= 2)
        {
            return input;
        }

        var deduped = new List<Point>(input.Count);
        deduped.Add(input[0]);

        for (var i = 1; i < input.Count; i++)
        {
            if (Distance(input[i], deduped[^1]) >= 1.0)
            {
                deduped.Add(input[i]);
            }
        }

        // Guard against accidental closure artifacts.
            // For loop lines, preserve the closure point; for linear routes, remove it
            if (!_lineConfiguration.LineInfo.IsLoop && deduped.Count > 3 && Distance(deduped[0], deduped[^1]) < 2.0)
        {
            deduped.RemoveAt(deduped.Count - 1);
        }

        // Remove tiny backtracking spikes that visually look like a loop.
        var cleaned = new List<Point>(deduped.Count);
        cleaned.Add(deduped[0]);
        for (var i = 1; i < deduped.Count - 1; i++)
        {
            var prev = cleaned[^1];
            var curr = deduped[i];
            var next = deduped[i + 1];

            var prevToCurr = Distance(prev, curr);
            var currToNext = Distance(curr, next);
            var prevToNext = Distance(prev, next);
            var tinySpike = prevToCurr < 10.0 && currToNext < 10.0 && prevToNext < 8.0;

            if (!tinySpike)
            {
                cleaned.Add(curr);
            }
        }

        cleaned.Add(deduped[^1]);
        return cleaned;
    }

    private static IReadOnlyList<Point> SmoothPolylineChaikin(IReadOnlyList<Point> points, int iterations)
    {
        if (points.Count < 3 || iterations <= 0)
        {
            return points;
        }

        var current = points.ToList();
        for (var iter = 0; iter < iterations; iter++)
        {
            if (current.Count < 3)
            {
                break;
            }

            var next = new List<Point>(current.Count * 2);
            next.Add(current[0]);

            for (var i = 0; i < current.Count - 1; i++)
            {
                var p0 = current[i];
                var p1 = current[i + 1];

                var q = new Point(0.75 * p0.X + 0.25 * p1.X, 0.75 * p0.Y + 0.25 * p1.Y);
                var r = new Point(0.25 * p0.X + 0.75 * p1.X, 0.25 * p0.Y + 0.75 * p1.Y);
                next.Add(q);
                next.Add(r);
            }

            next.Add(current[^1]);
            current = next;
        }

        return current;
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double LonToWorldX(double lon, int zoom)
    {
        var n = 256.0 * (1 << zoom);
        return (lon + 180.0) / 360.0 * n;
    }

    private static double LatToWorldY(double lat, int zoom)
    {
        var clamped = Math.Clamp(lat, -85.05112878, 85.05112878);
        var rad = clamped * Math.PI / 180.0;
        var merc = Math.Log(Math.Tan(Math.PI / 4.0 + rad / 2.0));
        var n = 256.0 * (1 << zoom);
        return (1.0 - merc / Math.PI) / 2.0 * n;
    }

    private static double LatToMercatorNormalized(double lat)
    {
        var clamped = Math.Clamp(lat, -85.05112878, 85.05112878);
        var rad = clamped * Math.PI / 180.0;
        var merc = Math.Log(Math.Tan(Math.PI / 4.0 + rad / 2.0));
        return (1.0 - merc / Math.PI) / 2.0;
    }

    private static HttpClient CreateOsmHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("JRETS-Go/1.0");
        return client;
    }

    private class StationMapDataJson
    {
        [JsonIgnore]
        public int Id { get; set; }

        [JsonPropertyName("displacement")]
        public double Displacement { get; set; }

        [JsonPropertyName("coordinates")]
        public double[] Coordinates { get; set; } = [];

        [JsonPropertyName("labeled")]
        public bool Labeled { get; set; } = false;
    }

    private class StationMapData
    {
        public int Id { get; set; }
        public double Displacement { get; set; }
        public double[] Coordinates { get; set; } = [];
        public bool Labeled { get; set; } = false;
    }

    private class RoutePathData
    {
        public Dictionary<double, double[]> RoutePoints { get; set; } = [];     
    }

    private sealed class RoutePointEntry
    {
        public required double DistanceKm { get; init; }

        public required double[] Coordinates { get; init; }
    }

    private sealed class RunningSegmentMapping
    {
        public required int FromStationId { get; init; }

        public required int ToStationId { get; init; }

        public required double ConfigFromMeters { get; init; }

        public required double ConfigToMeters { get; init; }

        public required double LoopLengthMeters { get; init; }

        public required double Scale { get; init; }

        public required double Offset { get; init; }
    }

    private sealed class MiniMapViewport
    {
        public required double Width { get; init; }

        public required double Height { get; init; }

        public required int Zoom { get; init; }

        public required double MinWorldX { get; init; }

        public required double MaxWorldX { get; init; }

        public required double MinWorldY { get; init; }

        public required double MaxWorldY { get; init; }

        public required double Scale { get; init; }

        public required double OffsetX { get; init; }

        public required double OffsetY { get; init; }
    }

}
