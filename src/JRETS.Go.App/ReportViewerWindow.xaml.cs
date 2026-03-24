using System.IO;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App;

public partial class ReportViewerWindow
{
    public ReportViewerWindow(DriveSessionReport report, string sourcePath)
    {
        InitializeComponent();

        HeaderTextBlock.Text = $"Drive Report  Total: {report.TotalScore:F1}";
        MetaTextBlock.Text =
            $"Source: {report.DataSource}  |  Start: {report.StartedAt:yyyy-MM-dd HH:mm:ss}  |  End: {report.EndedAt:yyyy-MM-dd HH:mm:ss}  |  File: {Path.GetFileName(sourcePath)}";

        StopsDataGrid.ItemsSource = report.Stops.Select(x => new StopRow
        {
            StationLabel = x.StationName,
            CapturedAtText = x.CapturedAt.ToString("HH:mm:ss"),
            PositionErrorMeters = x.PositionErrorMeters.ToString("F2"),
            TimeErrorSeconds = x.TimeErrorSeconds.ToString(),
            PositionScore = x.PositionScore.ToString("F1"),
            TimeScore = x.TimeScore.ToString("F1"),
            FinalScore = x.FinalScore.ToString("F1"),
            FinalScoreBarWidth = Math.Clamp(x.FinalScore, 0, 100) * 2.4
        }).ToArray();
    }

    private sealed class StopRow
    {
        public required string StationLabel { get; init; }

        public required string CapturedAtText { get; init; }

        public required string PositionErrorMeters { get; init; }

        public required string TimeErrorSeconds { get; init; }

        public required string PositionScore { get; init; }

        public required string TimeScore { get; init; }

        public required string FinalScore { get; init; }

        public required double FinalScoreBarWidth { get; init; }
    }
}
