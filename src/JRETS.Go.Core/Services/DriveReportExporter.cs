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
        builder.AppendLine("station_id,station_name,captured_at,scheduled_arrival_time,actual_arrival_time,actual_departure_time,position_error_meters,time_error_seconds,position_score,time_score,final_score,is_scored_stop");

        foreach (var stop in report.Stops)
        {
            var stationName = stop.StationName.Replace("\"", "\"\"");
            var scheduledArrival = stop.ScheduledArrivalSeconds.HasValue ? FormatClock(stop.ScheduledArrivalSeconds.Value) : string.Empty;
            var actualArrival = stop.ActualArrivalSeconds.HasValue ? FormatClock(stop.ActualArrivalSeconds.Value) : string.Empty;
            var actualDeparture = stop.ActualDepartureSeconds.HasValue ? FormatClock(stop.ActualDepartureSeconds.Value) : string.Empty;
            var positionError = stop.PositionErrorMeters.HasValue ? stop.PositionErrorMeters.Value.ToString("+0.00;-0.00;0.00") : string.Empty;
            var timeError = stop.TimeErrorSeconds.HasValue ? stop.TimeErrorSeconds.Value.ToString("+0;-0;0") : string.Empty;
            var positionScore = stop.PositionScore.HasValue ? stop.PositionScore.Value.ToString("F1") : string.Empty;
            var timeScore = stop.TimeScore.HasValue ? stop.TimeScore.Value.ToString("F1") : string.Empty;
            var finalScore = stop.FinalScore.HasValue ? stop.FinalScore.Value.ToString("F1") : string.Empty;
            builder.AppendLine(
                $"{stop.StationId},\"{stationName}\",{stop.CapturedAt:O},{scheduledArrival},{actualArrival},{actualDeparture},{positionError},{timeError},{positionScore},{timeScore},{finalScore},{stop.IsScoredStop}");
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
