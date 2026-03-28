using JRETS.Go.Core.Configuration;
using System.Collections;
using System.Globalization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JRETS.Go.Core.Services;

public sealed class YamlLineConfigurationLoader : ILineConfigurationLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public LineConfiguration LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Configuration path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Line configuration file was not found.", filePath);
        }

        var content = File.ReadAllText(filePath);
        var yaml = _deserializer.Deserialize<LineConfigurationYaml>(content)
            ?? throw new InvalidOperationException("Line configuration file is empty.");

        if (yaml.LineInfo is null)
        {
            throw new InvalidOperationException("line_info section is required.");
        }

        if (yaml.Stations is null || yaml.Stations.Count == 0)
        {
            throw new InvalidOperationException("stations section requires at least one station.");
        }

        return new LineConfiguration
        {
            LineInfo = yaml.LineInfo,
            TrainInfo = yaml.TrainInfo ?? [],
            MapInfo = yaml.MapInfo,
            // Keep station order exactly as defined in YAML.
            Stations = yaml.Stations
                .Select(MapStation)
                .ToArray()
        };
    }

    private static StationInfo MapStation(StationInfoYaml station)
    {
        return new StationInfo
        {
            Id = station.Id,
            Number = station.Number,
            NameJp = station.NameJp,
            NameEn = station.NameEn,
            Code = station.Code,
            Pa = ParsePa(station),
            Melody = station.Melody ?? [],
            SkipTrain = station.SkipTrain ?? []
        };
    }

    private static Dictionary<string, List<PaAnnouncementEntry>> ParsePa(StationInfoYaml station)
    {
        var result = new Dictionary<string, List<PaAnnouncementEntry>>(StringComparer.OrdinalIgnoreCase);
        if (station.Pa is null)
        {
            return result;
        }

        foreach (var (trainId, rawEntries) in station.Pa)
        {
            var entries = new List<PaAnnouncementEntry>(rawEntries.Count);
            for (var i = 0; i < rawEntries.Count; i++)
            {
                entries.Add(ParsePaEntry(rawEntries[i], station.Id, trainId, i));
            }

            result[trainId] = entries;
        }

        return result;
    }

    private static PaAnnouncementEntry ParsePaEntry(object rawEntry, int stationId, string trainId, int paIndex)
    {
        if (rawEntry is string fileName)
        {
            return new PaAnnouncementEntry
            {
                FileName = fileName
            };
        }

        if (rawEntry is IDictionary dictionary)
        {
            if (dictionary.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Invalid pa entry at station {stationId}, train {trainId}, index {paIndex}: mapping form must contain exactly one pair.");
            }

            var enumerator = dictionary.GetEnumerator();
            enumerator.MoveNext();

            var pair = (DictionaryEntry)enumerator.Current;
            var mappedFileName = pair.Key?.ToString();
            if (string.IsNullOrWhiteSpace(mappedFileName))
            {
                throw new InvalidOperationException(
                    $"Invalid pa entry at station {stationId}, train {trainId}, index {paIndex}: file name key is required.");
            }

            if (!TryConvertToDouble(pair.Value, out var triggerDistanceMeters))
            {
                throw new InvalidOperationException(
                    $"Invalid pa entry at station {stationId}, train {trainId}, index {paIndex}: trigger distance must be numeric.");
            }

            return new PaAnnouncementEntry
            {
                FileName = mappedFileName,
                TriggerDistanceMeters = triggerDistanceMeters
            };
        }

        throw new InvalidOperationException(
            $"Invalid pa entry at station {stationId}, train {trainId}, index {paIndex}: expected a string or a one-pair mapping.");
    }

    private static bool TryConvertToDouble(object? value, out double result)
    {
        switch (value)
        {
            case double doubleValue:
                result = doubleValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case decimal decimalValue:
                result = (double)decimalValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case short shortValue:
                result = shortValue;
                return true;
            case byte byteValue:
                result = byteValue;
                return true;
            case string stringValue:
                return double.TryParse(stringValue, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    private sealed class LineConfigurationYaml
    {
        public LineInfo? LineInfo { get; init; }

        public List<TrainInfo>? TrainInfo { get; init; }

        public MapInfo? MapInfo { get; init; }

        public List<StationInfoYaml>? Stations { get; init; }
    }

    private sealed class StationInfoYaml
    {
        public required int Id { get; init; }

        public required int Number { get; init; }

        public required string NameJp { get; init; }

        public required string NameEn { get; init; }

        public string? Code { get; init; }

        public Dictionary<string, List<object>>? Pa { get; init; }

        public List<string>? Melody { get; init; }

        public List<string>? SkipTrain { get; init; }
    }
}
