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
        builder.AppendLine("station_id,station_name,captured_at,position_error_meters,time_error_seconds,position_score,time_score,final_score");

        foreach (var stop in report.Stops)
        {
            var stationName = stop.StationName.Replace("\"", "\"\"");
            builder.AppendLine(
                $"{stop.StationId},\"{stationName}\",{stop.CapturedAt:O},{stop.PositionErrorMeters:+0.00;-0.00;0.00},{stop.TimeErrorSeconds},{stop.PositionScore:F1},{stop.TimeScore:F1},{stop.FinalScore:F1}");
        }

        File.WriteAllText(filePath, builder.ToString());
        return filePath;
    }
}
