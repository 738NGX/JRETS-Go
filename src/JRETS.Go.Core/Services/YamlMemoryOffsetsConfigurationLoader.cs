using JRETS.Go.Core.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JRETS.Go.Core.Services;

public sealed class YamlMemoryOffsetsConfigurationLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public MemoryOffsetsConfiguration LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Offsets config path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Offsets config file was not found.", filePath);
        }

        var content = File.ReadAllText(filePath);
        var yaml = _deserializer.Deserialize<MemoryOffsetsYaml>(content)
            ?? throw new InvalidOperationException("Offsets config file is empty.");

        if (yaml.ProcessName is null || yaml.ModuleName is null || yaml.Offsets is null)
        {
            throw new InvalidOperationException("process_name, module_name and offsets sections are required.");
        }

        return new MemoryOffsetsConfiguration
        {
            ProcessName = yaml.ProcessName,
            ModuleName = yaml.ModuleName,
            Offsets = new MemoryOffsets
            {
                NextStationId = ParseOffset(yaml.Offsets.NextStationId, nameof(yaml.Offsets.NextStationId)),
                DoorState = ParseOffset(yaml.Offsets.DoorState, nameof(yaml.Offsets.DoorState)),
                CurrentTimeSeconds = ParseOffset(yaml.Offsets.CurrentTimeSeconds, nameof(yaml.Offsets.CurrentTimeSeconds)),
                CurrentTimeMinutes = ParseOffset(yaml.Offsets.CurrentTimeMinutes, nameof(yaml.Offsets.CurrentTimeMinutes)),
                CurrentTimeHours = ParseOffset(yaml.Offsets.CurrentTimeHours, nameof(yaml.Offsets.CurrentTimeHours)),
                TimetableSecond = ParseOffset(yaml.Offsets.TimetableSecond, nameof(yaml.Offsets.TimetableSecond)),
                TimetableMinute = ParseOffset(yaml.Offsets.TimetableMinute, nameof(yaml.Offsets.TimetableMinute)),
                TimetableHour = ParseOffset(yaml.Offsets.TimetableHour, nameof(yaml.Offsets.TimetableHour)),
                CurrentDistance = ParseOffset(yaml.Offsets.CurrentDistance, nameof(yaml.Offsets.CurrentDistance)),
                TargetStopDistance = ParseOffset(yaml.Offsets.TargetStopDistance, nameof(yaml.Offsets.TargetStopDistance)),
                LinePath = ParseOptionalOffset(yaml.Offsets.LinePath)
            }
        };
    }

    private static long ParseOffset(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Offset {fieldName} is required.");
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(value[2..], 16);
        }

        return Convert.ToInt64(value, 10);
    }

    private static long ParseOptionalOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return Convert.ToInt64(value[2..], 16);
        }

        return Convert.ToInt64(value, 10);
    }

    private sealed class MemoryOffsetsYaml
    {
        public string? ProcessName { get; init; }

        public string? ModuleName { get; init; }

        public OffsetsYaml? Offsets { get; init; }
    }

    private sealed class OffsetsYaml
    {
        public string? NextStationId { get; init; }

        public string? DoorState { get; init; }

        public string? CurrentTimeSeconds { get; init; }

        public string? CurrentTimeMinutes { get; init; }

        public string? CurrentTimeHours { get; init; }

        public string? TimetableSecond { get; init; }

        public string? TimetableMinute { get; init; }

        public string? TimetableHour { get; init; }

        public string? CurrentDistance { get; init; }

        public string? TargetStopDistance { get; init; }

        public string? LinePath { get; init; }
    }
}
