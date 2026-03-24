using System.Text.Json;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.Core.Services;

public sealed class DriveReportReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DriveSessionReport Load(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Report path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Report file was not found.", filePath);
        }

        var content = File.ReadAllText(filePath);
        var report = JsonSerializer.Deserialize<DriveSessionReport>(content, JsonOptions);
        if (report is null)
        {
            throw new InvalidOperationException("Report content is invalid.");
        }

        return report;
    }

    public string? FindLatestJsonReportPath(string reportsDirectory)
    {
        if (string.IsNullOrWhiteSpace(reportsDirectory) || !Directory.Exists(reportsDirectory))
        {
            return null;
        }

        return Directory.GetFiles(reportsDirectory, "drive-report-*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
