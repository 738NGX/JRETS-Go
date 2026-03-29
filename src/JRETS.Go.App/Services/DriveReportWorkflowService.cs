using System;
using System.Collections.Generic;
using System.Linq;
using JRETS.Go.Core.Runtime;
using JRETS.Go.Core.Services;

namespace JRETS.Go.App.Services;

public sealed class DriveReportWorkflowService
{
    public double GetTotalScore(IReadOnlyList<StationStopScore> stationScores)
    {
        if (stationScores.Count == 0)
        {
            return 0;
        }

        return Math.Round(stationScores.Sum(x => x.FinalScore ?? 0), 1);
    }

    public DriveSessionReport BuildReport(
        DateTime startedAt,
        DateTime endedAt,
        bool usingLiveMemory,
        double totalScore,
        double sessionDistanceMeters,
        IReadOnlyList<StationStopScore> stationScores,
        string? reportTrainNumber,
        string? serviceType,
        string? lineId,
        string? lineCode,
        string? lineName,
        string? lineColor,
        string directionText,
        string? originStationName)
    {
        var firstStation = originStationName ?? stationScores.FirstOrDefault()?.StationName;
        var lastStation = stationScores.LastOrDefault()?.StationName;
        var segmentText = string.IsNullOrWhiteSpace(firstStation) || string.IsNullOrWhiteSpace(lastStation)
            ? string.Empty
            : $"{firstStation} -> {lastStation}";

        return new DriveSessionReport
        {
            StartedAt = startedAt,
            EndedAt = endedAt,
            DataSource = usingLiveMemory ? "LiveMemory" : "Debug",
            TotalScore = totalScore,
            DistanceMeters = Math.Round(sessionDistanceMeters, 1),
            Metadata = new DriveSessionMetadata
            {
                TrainNumber = reportTrainNumber,
                ServiceType = serviceType,
                LineId = lineId,
                LineCode = lineCode,
                LineName = lineName,
                LineColor = lineColor,
                DirectionText = directionText,
                SegmentText = segmentText,
                OriginStationName = originStationName
            },
            Stops = stationScores.ToArray()
        };
    }

    public (string JsonPath, string CsvPath) ExportReport(
        DriveReportExporter exporter,
        string reportsDirectory,
        DriveSessionReport report)
    {
        var jsonPath = exporter.Export(reportsDirectory, report);
        var csvPath = exporter.ExportCsv(reportsDirectory, report);
        return (jsonPath, csvPath);
    }

    public string? FindLatestReportPath(DriveReportReader reader, string reportsDirectory)
    {
        return reader.FindLatestJsonReportPath(reportsDirectory);
    }
}
