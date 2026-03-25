using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;
using JRETS.Go.Core.Services;

namespace JRETS.Go.App;

public partial class ReportViewerWindow
{
    private static readonly Brush DefaultLineBrush = new SolidColorBrush(Color.FromRgb(120, 164, 210));
    private readonly DriveReportReader _reportReader = new();
    private readonly Dictionary<string, LineConfiguration> _lineConfigById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LineConfiguration> _lineConfigByCode = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _reportsDirectory;
    private readonly List<DriveLogEntry> _entries = [];

    public ReportViewerWindow(DriveSessionReport report, string sourcePath, string reportsDirectory, string configsDirectory)
    {
        InitializeComponent();

        _reportsDirectory = reportsDirectory;
        LoadLineConfigurations(configsDirectory);

        _entries.AddRange(LoadDriveLogEntries(report, sourcePath, reportsDirectory));
        LogsListBox.ItemsSource = _entries;
        ApplySummary(_entries);

        var selected = _entries.FirstOrDefault(x =>
            string.Equals(x.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase)) ?? _entries.FirstOrDefault();

        LogsListBox.SelectedItem = selected;
        if (selected is not null)
        {
            ApplyDetail(selected);
            return;
        }

        ApplyEmptyDetail();
    }

    private void LogsListBoxSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogsListBox.SelectedItem is not DriveLogEntry entry)
        {
            return;
        }

        ApplyDetail(entry);
    }

    private void DeleteSelectedLogClick(object sender, RoutedEventArgs e)
    {
        if (LogsListBox.SelectedItem is not DriveLogEntry selected)
        {
            return;
        }

        var result = MessageBox.Show(
            $"确认删除日志文件 {Path.GetFileName(selected.SourcePath)} ?",
            "删除日志",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            if (File.Exists(selected.SourcePath))
            {
                File.Delete(selected.SourcePath);
            }

            var csvPath = Path.ChangeExtension(selected.SourcePath, ".csv");
            if (!string.IsNullOrWhiteSpace(csvPath) && File.Exists(csvPath))
            {
                File.Delete(csvPath);
            }

            _entries.Remove(selected);
            LogsListBox.Items.Refresh();
            ApplySummary(_entries);

            if (_entries.Count == 0)
            {
                ApplyEmptyDetail();
                return;
            }

            LogsListBox.SelectedItem = _entries[0];
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败: {ex.Message}", "删除日志", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplySummary(IReadOnlyList<DriveLogEntry> entries)
    {
        var totalDistanceMeters = entries.Sum(x => x.Report.DistanceMeters);
        var totalDuration = TimeSpan.FromTicks(entries.Sum(x => x.Duration.Ticks));
        var totalStops = entries.Sum(x => x.Report.Stops.Count);

        var allScoredStops = entries
            .SelectMany(x => x.Report.Stops)
            .Where(x => x.IsScoredStop)
            .ToList();

        var avgPositionErrorCm = allScoredStops.Count == 0
            ? 0
            : allScoredStops.Average(x => Math.Abs(x.PositionErrorMeters ?? 0) * 100);
        var avgTimeErrorSeconds = allScoredStops.Count == 0
            ? 0
            : allScoredStops.Average(x => Math.Abs(x.TimeErrorSeconds ?? 0));

        SummaryDistanceTextBlock.Text = $"Total Driving Distance: {totalDistanceMeters / 1000:F1} km";
        SummaryDurationTextBlock.Text = $"Total Driving Hours: {FormatDuration(totalDuration)}";
        SummaryStopsTextBlock.Text = $"Station Stop Times: {totalStops}";
        SummaryPositionErrorTextBlock.Text = $"Average Position Error: ±{avgPositionErrorCm:F1} cm";
        SummaryTimeErrorTextBlock.Text = $"Average Time Error: ±{avgTimeErrorSeconds:F1} s";
    }

    private void ApplyDetail(DriveLogEntry entry)
    {
        var report = entry.Report;
        HeaderTextBlock.Text = $"Total Score: {report.TotalScore:F1}";
        MetaTextBlock.Text =
            $"Source: {report.DataSource}  |  Start: {report.StartedAt:yyyy-MM-dd HH:mm:ss}  |  End: {report.EndedAt:yyyy-MM-dd HH:mm:ss}  |  File: {Path.GetFileName(entry.SourcePath)}";

        StopsDataGrid.ItemsSource = report.Stops.Select(x => new StopRow
        {
            StationLabel = x.StationName,
            CapturedAtText = x.CapturedAt.ToString("HH:mm:ss"),
            ScheduledArrivalText = FormatClockNullable(x.ScheduledArrivalSeconds),
            ActualArrivalText = FormatClockNullable(x.ActualArrivalSeconds),
            ActualDepartureText = FormatClockNullable(x.ActualDepartureSeconds),
            PositionErrorMeters = x.PositionErrorMeters.HasValue ? x.PositionErrorMeters.Value.ToString("F2") : "--",
            TimeErrorSeconds = x.TimeErrorSeconds.HasValue ? x.TimeErrorSeconds.Value.ToString("+0;-0;0") : "--",
            TotalScore = x.FinalScore.HasValue ? x.FinalScore.Value.ToString("F1") : "--"
        }).ToArray();
    }

    private void ApplyEmptyDetail()
    {
        HeaderTextBlock.Text = "Drive Logs";
        MetaTextBlock.Text = "No report data.";
        StopsDataGrid.ItemsSource = Array.Empty<StopRow>();
    }

    private List<DriveLogEntry> LoadDriveLogEntries(DriveSessionReport fallbackReport, string fallbackPath, string reportsDirectory)
    {
        var entries = new List<DriveLogEntry>();

        if (Directory.Exists(reportsDirectory))
        {
            foreach (var path in Directory.GetFiles(reportsDirectory, "drive-report-*.json").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                try
                {
                    var report = _reportReader.Load(path);
                    entries.Add(BuildDriveLogEntry(report, path));
                }
                catch
                {
                    // Skip broken report files and continue loading the rest.
                }
            }
        }

        if (entries.Count == 0)
        {
            entries.Add(BuildDriveLogEntry(fallbackReport, fallbackPath));
        }

        return entries;
    }

    private DriveLogEntry BuildDriveLogEntry(DriveSessionReport report, string sourcePath)
    {
        var metadata = report.Metadata;
        var configuration = ResolveLineConfiguration(metadata);

        var lineCode = FirstNonEmpty(metadata?.LineCode, configuration?.LineInfo.Code, "--");
        var lineName = FirstNonEmpty(metadata?.LineName, configuration?.LineInfo.NameJp, "--");
        var lineColorText = FirstNonEmpty(metadata?.LineColor, configuration?.LineInfo.LineColor, "#78A4D2");
        var direction = ResolveDirectionText(report, configuration);
        var segment = FirstNonEmpty(metadata?.SegmentText, BuildSegmentText(report), "--");
        var trainNo = FirstNonEmpty(metadata?.TrainNumber, "--");
        var serviceType = ToServiceLabel(FirstNonEmpty(metadata?.ServiceType, "Local"));
        var duration = report.EndedAt > report.StartedAt ? report.EndedAt - report.StartedAt : TimeSpan.Zero;
        var scoredStationCount = report.Stops.Count(x => x.IsScoredStop && x.FinalScore.HasValue);
        var averageScore = scoredStationCount > 0 ? report.TotalScore / scoredStationCount : 0;

        return new DriveLogEntry
        {
            SourcePath = sourcePath,
            Report = report,
            Duration = duration,
            DateText = report.StartedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            TrainAndTypeText = $"{trainNo} | {serviceType}",
            LineBadgeText = $"{lineCode} {lineName}",
            LineColorBrush = BuildLineBrush(lineColorText),
            DirectionText = direction,
            SegmentText = segment,
            AverageScoreText = averageScore.ToString("F1"),
            ItemBackground = new SolidColorBrush(Color.FromRgb(21, 36, 51))
        };
    }

    private void LoadLineConfigurations(string configsDirectory)
    {
        _lineConfigById.Clear();
        _lineConfigByCode.Clear();

        if (!Directory.Exists(configsDirectory))
        {
            return;
        }

        var loader = new YamlLineConfigurationLoader();
        foreach (var file in Directory.GetFiles(configsDirectory, "*.yaml"))
        {
            try
            {
                var config = loader.LoadFromFile(file);
                if (!string.IsNullOrWhiteSpace(config.LineInfo.Id))
                {
                    _lineConfigById[config.LineInfo.Id] = config;
                }

                if (!string.IsNullOrWhiteSpace(config.LineInfo.Code))
                {
                    _lineConfigByCode[config.LineInfo.Code] = config;
                }
            }
            catch
            {
                // Ignore non-line configuration yaml files.
            }
        }
    }

    private LineConfiguration? ResolveLineConfiguration(DriveSessionMetadata? metadata)
    {
        if (metadata is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(metadata.LineId)
            && _lineConfigById.TryGetValue(metadata.LineId, out var byId))
        {
            return byId;
        }

        if (!string.IsNullOrWhiteSpace(metadata.LineCode)
            && _lineConfigByCode.TryGetValue(metadata.LineCode, out var byCode))
        {
            return byCode;
        }

        return null;
    }

    private static string ResolveDirectionText(DriveSessionReport report, LineConfiguration? configuration)
    {
        var metadataDirection = report.Metadata?.DirectionText;
        if (!string.IsNullOrWhiteSpace(metadataDirection))
        {
            return metadataDirection;
        }

        if (configuration is not null
            && (string.Equals(configuration.LineInfo.Id, "yamanote", StringComparison.OrdinalIgnoreCase)
                || configuration.LineInfo.NameJp.Contains("山手", StringComparison.Ordinal)))
        {
            return "内回り";
        }

        var trainNumber = report.Metadata?.TrainNumber;
        if (configuration is not null && !string.IsNullOrWhiteSpace(trainNumber))
        {
            var train = configuration.TrainInfo.FirstOrDefault(x => string.Equals(x.Id, trainNumber, StringComparison.OrdinalIgnoreCase));
            if (train is not null)
            {
                var terminal = configuration.Stations.FirstOrDefault(x => x.Id == train.Terminal);
                if (terminal is not null)
                {
                    return $"{terminal.NameJp} 行";
                }
            }
        }

        return "--";
    }

    private static string BuildSegmentText(DriveSessionReport report)
    {
        var first = report.Metadata?.OriginStationName ?? report.Stops.FirstOrDefault()?.StationName;
        var last = report.Stops.LastOrDefault()?.StationName;
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(last))
        {
            return string.Empty;
        }

        return $"{first} -> {last}";
    }

    private static Brush BuildLineBrush(string lineColorText)
    {
        try
        {
            var converter = new BrushConverter();
            if (converter.ConvertFromString(lineColorText) is Brush brush)
            {
                return brush;
            }
        }
        catch
        {
            // Use default brush when color parsing fails.
        }

        return DefaultLineBrush;
    }

    private static string ToServiceLabel(string rawType)
    {
        return rawType.Trim().ToLowerInvariant() switch
        {
            "local" => "各駅停車",
            "rapid" => "快速",
            _ => rawType
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var totalHours = (int)duration.TotalHours;
        return $"{totalHours:D2}:{duration.Minutes:D2}:{duration.Seconds:D2}";
    }

    private sealed class StopRow
    {
        public required string StationLabel { get; init; }

        public required string CapturedAtText { get; init; }

        public required string ScheduledArrivalText { get; init; }

        public required string ActualArrivalText { get; init; }

        public required string ActualDepartureText { get; init; }

        public required string PositionErrorMeters { get; init; }

        public required string TimeErrorSeconds { get; init; }

        public required string TotalScore { get; init; }
    }

    private sealed class DriveLogEntry
    {
        public required string SourcePath { get; init; }

        public required DriveSessionReport Report { get; init; }

        public required TimeSpan Duration { get; init; }

        public required string DateText { get; init; }

        public required string TrainAndTypeText { get; init; }

        public required string LineBadgeText { get; init; }

        public required Brush LineColorBrush { get; init; }

        public required string DirectionText { get; init; }

        public required string SegmentText { get; init; }

        public required string AverageScoreText { get; init; }

        public required Brush ItemBackground { get; init; }
    }

    private static string FormatClock(int totalSeconds)
    {
        var normalized = ((totalSeconds % 86400) + 86400) % 86400;
        var hours = normalized / 3600;
        var minutes = (normalized % 3600) / 60;
        var seconds = normalized % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    private static string FormatClockNullable(int? totalSeconds)
    {
        return totalSeconds.HasValue ? FormatClock(totalSeconds.Value) : "--";
    }
}
