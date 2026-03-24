using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;
using JRETS.Go.Core.Services;

namespace JRETS.Go.Core.Tests;

public class UnitTest1
{
    [Fact]
    public void LoadFromFile_ReturnsStationsFromYaml()
    {
        var yamlPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(yamlPath, """
line_info:
  name_jp: Test Line
  name_en: Test Line
  line_color: "#123456"
  code: TS
stations:
  - id: 2
    number: 2
    name_jp: B
    name_en: B
  - id: 1
    number: 1
    name_jp: A
    name_en: A
""");

            var loader = new YamlLineConfigurationLoader();
            var result = loader.LoadFromFile(yamlPath);

            Assert.Equal("TS", result.LineInfo.Code);
            Assert.Equal(2, result.Stations.Count);
            Assert.Equal(1, result.Stations[0].Id);
            Assert.Equal(2, result.Stations[1].Id);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void Resolve_WhenDoorOpen_ShowsCurrentStop()
    {
        var line = new LineConfiguration
        {
            LineInfo = new LineInfo
            {
                NameJp = "Line",
                NameEn = "Line",
                LineColor = "#FFFFFF",
                Code = "L"
            },
            TrainInfo = [],
            Stations =
            [
                new StationInfo { Id = 100, Number = 1, NameJp = "A", NameEn = "A" },
                new StationInfo { Id = 101, Number = 2, NameJp = "B", NameEn = "B" }
            ]
        };

        var snapshot = new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = 101,
            DoorOpen = true,
            MainClockSeconds = 0,
            TimetableHour = 0,
            TimetableMinute = 0,
            TimetableSecond = 0,
            CurrentDistanceMeters = 0,
            TargetStopDistanceMeters = 0
        };

        var resolver = new DisplayStateResolver();
        var state = resolver.Resolve(line, snapshot);

        Assert.Equal("B", state.CurrentStopStation?.NameJp);
        Assert.Equal("Stopped: B", state.DisplayText);
    }

    [Fact]
    public void LoadOffsetsFromFile_ParsesHexOffsets()
    {
        var yamlPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(yamlPath, """
process_name: JREAST_TrainSimulator.exe
module_name: JREAST_TrainSimulator.exe
offsets:
  next_station_id: "0x110B1D8"
  door_state: "0x1765F60"
  main_clock_seconds: "0x14AAE84"
  timetable_second: "0x174907C"
  timetable_minute: "0x1749080"
  timetable_hour: "0x1749084"
  current_distance: "0x14AAE18"
  target_stop_distance: "0x10BEDF0"
""");

            var loader = new YamlMemoryOffsetsConfigurationLoader();
            var result = loader.LoadFromFile(yamlPath);

            Assert.Equal("JREAST_TrainSimulator.exe", result.ProcessName);
            Assert.Equal(0x110B1D8, result.Offsets.NextStationId);
            Assert.Equal(0x10BEDF0, result.Offsets.TargetStopDistance);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void ScoreStop_ReturnsWeightedScore()
    {
        var station = new StationInfo
        {
            Id = 33201,
            Number = 46,
            NameJp = "Station",
            NameEn = "Station"
        };

        var snapshot = new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = 33202,
            DoorOpen = true,
            MainClockSeconds = 3605,
            TimetableHour = 1,
            TimetableMinute = 0,
            TimetableSecond = 0,
            CurrentDistanceMeters = 999.5,
            TargetStopDistanceMeters = 1000
        };

        var service = new StopScoringService();
        var result = service.ScoreStop(station, snapshot);

        Assert.Equal(-0.5, result.PositionErrorMeters);
        Assert.Equal(5, result.TimeErrorSeconds);
        Assert.True(result.FinalScore > 90);
    }

    [Fact]
    public void ExportReport_WritesJsonFile()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"jrets-go-tests-{Guid.NewGuid():N}");

        try
        {
            var report = new DriveSessionReport
            {
                StartedAt = DateTime.Now,
                EndedAt = DateTime.Now,
                DataSource = "Debug",
                TotalScore = 88.5,
                Stops =
                [
                    new StationStopScore
                    {
                        StationId = 1,
                        StationName = "A",
                        CapturedAt = DateTime.Now,
                        PositionErrorMeters = 0.2,
                        TimeErrorSeconds = 3,
                        PositionScore = 99.6,
                        TimeScore = 97,
                        FinalScore = 98.6
                    }
                ]
            };

            var exporter = new DriveReportExporter();
            var path = exporter.Export(tempDirectory, report);
            var csvPath = exporter.ExportCsv(tempDirectory, report);

            Assert.True(File.Exists(path));
            Assert.True(File.Exists(csvPath));
            Assert.Contains("drive-report-", Path.GetFileName(path));
            Assert.EndsWith(".csv", Path.GetFileName(csvPath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }

    [Fact]
    public void LoadScoringConfig_FromYaml_Works()
    {
        var yamlPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(yamlPath, """
scoring:
  position_penalty_per_meter: 3.0
  time_penalty_per_second: 0.5
  position_weight: 0.7
  time_weight: 0.3
  max_score_per_stop: 120
""");

            var loader = new YamlScoringConfigurationLoader();
            var config = loader.LoadFromFile(yamlPath);

            Assert.Equal(3.0, config.PositionPenaltyPerMeter);
            Assert.Equal(0.5, config.TimePenaltyPerSecond);
            Assert.Equal(0.7, config.PositionWeight);
            Assert.Equal(0.3, config.TimeWeight);
            Assert.Equal(120, config.MaxScorePerStop);
        }
        finally
        {
            File.Delete(yamlPath);
        }
    }

    [Fact]
    public void ScoreStop_UsesInjectedConfig()
    {
        var station = new StationInfo
        {
            Id = 1,
            Number = 1,
            NameJp = "A",
            NameEn = "A"
        };

        var snapshot = new RealtimeSnapshot
        {
            CapturedAt = DateTime.Now,
            NextStationId = 2,
            DoorOpen = true,
            MainClockSeconds = 1010,
            TimetableHour = 0,
            TimetableMinute = 16,
            TimetableSecond = 40,
            CurrentDistanceMeters = 110,
            TargetStopDistanceMeters = 100
        };

        var config = new ScoringConfiguration
        {
            PositionPenaltyPerMeter = 1.0,
            TimePenaltyPerSecond = 0.5,
            PositionWeight = 0.7,
            TimeWeight = 0.3,
            MaxScorePerStop = 100
        };

        var service = new StopScoringService(config);
        var result = service.ScoreStop(station, snapshot);

        Assert.Equal(10, result.PositionErrorMeters);
        Assert.Equal(10, result.TimeErrorSeconds);
        Assert.Equal(91.5, result.FinalScore);
    }

    [Fact]
    public void ReportReader_LoadAndFindLatest_Works()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"jrets-go-tests-{Guid.NewGuid():N}");

        try
        {
            var report = new DriveSessionReport
            {
                StartedAt = DateTime.Now,
                EndedAt = DateTime.Now,
                DataSource = "Debug",
                TotalScore = 90,
                Stops =
                [
                    new StationStopScore
                    {
                        StationId = 1,
                        StationName = "A",
                        CapturedAt = DateTime.Now,
                        PositionErrorMeters = 1,
                        TimeErrorSeconds = 2,
                        PositionScore = 98,
                        TimeScore = 97,
                        FinalScore = 97.6
                    }
                ]
            };

            var exporter = new DriveReportExporter();
            var path = exporter.Export(tempDirectory, report);

            var reader = new DriveReportReader();
            var latest = reader.FindLatestJsonReportPath(tempDirectory);
            var loaded = reader.Load(path);

            Assert.Equal(path, latest);
            Assert.Equal(90, loaded.TotalScore);
            Assert.Single(loaded.Stops);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }
    }
}

