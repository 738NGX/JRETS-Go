using System.Text.Json;
using System.Text;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class DriveReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string Export(string outputDirectory, DriveSessionReport report)
    {
        Directory.CreateDirectory(outputDirectory);

        var fileName = $"drive-report-{report.StartedAt:yyyyMMdd-HHmmss}.json";
        var filePath = Path.Combine(outputDirectory, fileName);

        var content = JsonSerializer.Serialize(report, JsonOptions);
        File.WriteAllText(filePath, content);

        return filePath;
    }

    public string ExportCsv(string outputDirectory, DriveSessionReport report)
    {
        Directory.CreateDirectory(outputDirectory);

        var fileName = $"drive-report-{report.StartedAt:yyyyMMdd-HHmmss}.csv";
        var filePath = Path.Combine(outputDirectory, fileName);

        var builder = new StringBuilder();
        builder.AppendLine("station_id,station_name,captured_at,scheduled_arrival_time,actual_arrival_time,position_error_meters,time_error_seconds,position_score,time_score,final_score");

        foreach (var stop in report.Stops)
        {
            var stationName = stop.StationName.Replace("\"", "\"\"");
            var scheduledArrival = FormatClock(stop.ScheduledArrivalSeconds);
            var actualArrival = FormatClock(stop.ActualArrivalSeconds);
            builder.AppendLine(
                $"{stop.StationId},\"{stationName}\",{stop.CapturedAt:O},{scheduledArrival},{actualArrival},{stop.PositionErrorMeters:+0.00;-0.00;0.00},{stop.TimeErrorSeconds:+0;-0;0},{stop.PositionScore:F1},{stop.TimeScore:F1},{stop.FinalScore:F1}");
        }

        File.WriteAllText(filePath, builder.ToString());
        return filePath;
    }

    private static string FormatClock(int totalSeconds)
    {
        var normalized = ((totalSeconds % 86400) + 86400) % 86400;
        var hours = normalized / 3600;
        var minutes = (normalized % 3600) / 60;
        var seconds = normalized % 60;
        return $"{hours:D2}:{minutes:D2}:{seconds:D2}";
    }
}
