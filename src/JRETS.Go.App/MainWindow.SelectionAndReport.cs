using System;
using System.IO;
using System.Linq;
using System.Windows;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App;

public partial class MainWindow
{
    private void ExportSessionReport()
    {
        var currentSnapshot = GetCurrentSnapshot();
        var currentState = _displayStateResolver.Resolve(_lineConfiguration, currentSnapshot);
        var reportTrainNumber = ResolveReportTrainNumber(currentSnapshot);

        var defaultDirection = string.Equals(_lineConfiguration.LineInfo.Id, "yamanote", StringComparison.OrdinalIgnoreCase)
            ? "内回り"
            : GetDirectionText(currentState);

        var report = _driveReportWorkflowService.BuildReport(
            startedAt: _sessionStartedAt,
            endedAt: DateTime.Now,
            usingLiveMemory: _usingLiveMemory,
            totalScore: GetTotalScore(),
            sessionDistanceMeters: _sessionDistanceMeters,
            stationScores: _stationScores,
            reportTrainNumber: reportTrainNumber,
            serviceType: _selectedService?.Train.Type,
            lineId: _lineConfiguration.LineInfo.Id,
            lineCode: _lineConfiguration.LineInfo.Code,
            lineName: _lineConfiguration.LineInfo.NameJp,
            lineColor: _lineConfiguration.LineInfo.LineColor,
            directionText: defaultDirection,
            originStationName: _originStationName);

        var reportsDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        var exportResult = _driveReportWorkflowService.ExportReport(_driveReportExporter, reportsDirectory, report);
        var jsonPath = exportResult.JsonPath;
        var csvPath = exportResult.CsvPath;
        _lastDataSourceError = $"Report exported: {Path.GetFileName(jsonPath)}, {Path.GetFileName(csvPath)}";
    }

    private void OpenLatestReport()
    {
        // If window already exists, just toggle visibility
        if (_reportViewerWindow != null)
        {
            ToggleReportWindow();
            return;
        }

        var reportsDirectory = Path.Combine(AppContext.BaseDirectory, "reports");
        var latestPath = _driveReportWorkflowService.FindLatestReportPath(_driveReportReader, reportsDirectory);
        if (string.IsNullOrWhiteSpace(latestPath))
        {
            _lastDataSourceError = "No report file found.";
            UpdateDisplay();
            return;
        }

        try
        {
            var report = _driveReportReader.Load(latestPath);
            var lineConfigsDirectory = Path.Combine(AppContext.BaseDirectory, "configs", "lines");
            _reportViewerWindow = new ReportViewerWindow(report, latestPath, reportsDirectory, lineConfigsDirectory);
            _reportViewerWindow.Closed += (sender, e) =>
            {
                _reportViewerWindow = null;
            };
            _reportViewerWindow.Show();
        }
        catch (Exception ex)
        {
            _lastDataSourceError = $"Open report failed: {ex.Message}";
            UpdateDisplay();
        }
    }

    private void ToggleReportWindow()
    {
        if (_reportViewerWindow == null)
        {
            OpenLatestReport();
            return;
        }

        if (_reportViewerWindow.Visibility == Visibility.Visible)
        {
            _reportViewerWindow.Hide();
        }
        else
        {
            _reportViewerWindow.Show();
            _reportViewerWindow.Activate();
        }
    }

    private bool TryApplyAutoLineAndServiceSelection(out string errorMessage)
    {
        errorMessage = string.Empty;

        if (_memoryDataSource is null)
        {
            errorMessage = "Error: Memory data source is not initialized.";
            return false;
        }

        if (!_memoryDataSource.TryAttach())
        {
            errorMessage = $"Error: Failed to attach to process: {_memoryDataSource.LastAttachError}";
            return false;
        }

        RealtimeSnapshot snapshot;
        try
        {
            snapshot = _memoryDataSource.GetSnapshot();
        }
        catch (Exception ex)
        {
            errorMessage = $"Error: Failed to read line_path: {ex.Message}";
            return false;
        }

        _activeLinePath = snapshot.LinePath;
        var normalizedPath = NormalizeLinePath(snapshot.LinePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            errorMessage = "運転外 / Not Driving";
            return false;
        }

        if (!TryResolveLineAndTrainByPath(normalizedPath, out var mapping))
        {
            errorMessage = "未対応 / Not supported";
            return false;
        }

        var lineOption = _lineConfigOptions.FirstOrDefault(x =>
            string.Equals(x.Configuration.LineInfo.Id, mapping.LineId, StringComparison.OrdinalIgnoreCase));
        if (lineOption is null)
        {
            errorMessage = $"Error: Line configuration not found: {mapping.LineId}";
            return false;
        }

        ApplyLineConfiguration(lineOption);
        SelectServiceByTrainId(mapping.TrainId);
        if (_selectedService is null)
        {
            errorMessage = $"Error: Line {mapping.LineId} is missing a timetable: {mapping.TrainId}";
            return false;
        }

        return true;
    }

    private bool TryResolveLineAndTrainByPath(string normalizedPath, out LinePathMappingEntry mapping)
    {
        if (_linePathMappingByPath.TryGetValue(normalizedPath, out var found) && found is not null)
        {
            mapping = found;
            return true;
        }

        foreach (var pair in _linePathMappingByPath)
        {
            if (normalizedPath.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
            {
                mapping = pair.Value;
                return true;
            }
        }

        mapping = null!;
        return false;
    }

    private string? ResolveReportTrainNumber(RealtimeSnapshot snapshot)
    {
        var normalizedPath = NormalizeLinePath(_activeLinePath ?? snapshot.LinePath);
        if (!string.IsNullOrWhiteSpace(normalizedPath)
            && TryResolveLineAndTrainByPath(normalizedPath, out var mapping)
            && !string.IsNullOrWhiteSpace(mapping.DiagramId))
        {
            return mapping.DiagramId;
        }

        return _selectedService?.Train.Id;
    }

    private static string NormalizeLinePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim().Replace('\\', '/').ToLowerInvariant();
        return normalized.TrimStart('/');
    }

    private void SelectServiceByTrainId(string trainId)
    {
        if (string.IsNullOrWhiteSpace(trainId))
        {
            return;
        }

        var target = _serviceOptions.FirstOrDefault(x =>
            string.Equals(x.Train.Id, trainId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return;
        }

        _suppressComboChange = true;
        ServiceTypeComboBox.SelectedItem = target;
        _suppressComboChange = false;
        _selectedService = target;
    }

}
