using JRETS.Go.Core.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JRETS.Go.Core.Services;

public sealed class YamlMemoryOffsetsConfigurationLoader
{
    private static readonly HashSet<string> SupportedSignatureFields = new(StringComparer.Ordinal)
    {
        "next_station_id",
        "door_state",
        "current_time_seconds",
        "current_time_minutes",
        "current_time_hours",
        "timetable_second",
        "timetable_minute",
        "timetable_hour",
        "current_distance",
        "target_stop_distance",
        "line_path"
    };

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

        if (yaml.ProcessName is null || yaml.ModuleName is null)
        {
            throw new InvalidOperationException("process_name and module_name are required.");
        }

        var signatures = ParseSignatures(yaml.Signatures);
        var offsets = yaml.Offsets ?? new OffsetsYaml();

        return new MemoryOffsetsConfiguration
        {
            ProcessName = yaml.ProcessName,
            ModuleName = yaml.ModuleName,
            Offsets = new MemoryOffsets
            {
                NextStationId = ParseOffsetOrSignature(offsets.NextStationId, "next_station_id", signatures),
                DoorState = ParseOffsetOrSignature(offsets.DoorState, "door_state", signatures),
                CurrentTimeSeconds = ParseOffsetOrSignature(offsets.CurrentTimeSeconds, "current_time_seconds", signatures),
                CurrentTimeMinutes = ParseOffsetOrSignature(offsets.CurrentTimeMinutes, "current_time_minutes", signatures),
                CurrentTimeHours = ParseOffsetOrSignature(offsets.CurrentTimeHours, "current_time_hours", signatures),
                TimetableSecond = ParseOffsetOrSignature(offsets.TimetableSecond, "timetable_second", signatures),
                TimetableMinute = ParseOffsetOrSignature(offsets.TimetableMinute, "timetable_minute", signatures),
                TimetableHour = ParseOffsetOrSignature(offsets.TimetableHour, "timetable_hour", signatures),
                CurrentDistance = ParseOffsetOrSignature(offsets.CurrentDistance, "current_distance", signatures),
                TargetStopDistance = ParseOffsetOrSignature(offsets.TargetStopDistance, "target_stop_distance", signatures),
                LinePath = ParseOptionalOffset(offsets.LinePath)
            },
            Signatures = signatures
        };
    }

    private static long ParseOffsetOrSignature(
        string? value,
        string fieldName,
        IReadOnlyDictionary<string, MemoryAddressSignature> signatures)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return ParseOffset(value, fieldName);
        }

        if (signatures.ContainsKey(fieldName))
        {
            return 0;
        }

        throw new InvalidOperationException($"Offset {fieldName} is required unless signatures.{fieldName} is configured.");
    }

    private static IReadOnlyDictionary<string, MemoryAddressSignature> ParseSignatures(
        IReadOnlyDictionary<string, SignatureYaml>? signatures)
    {
        if (signatures is null || signatures.Count == 0)
        {
            return new Dictionary<string, MemoryAddressSignature>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, MemoryAddressSignature>(StringComparer.Ordinal);
        foreach (var (fieldName, signature) in signatures)
        {
            if (string.IsNullOrWhiteSpace(fieldName) || signature is null || string.IsNullOrWhiteSpace(signature.Pattern))
            {
                throw new InvalidOperationException("Each signatures entry requires a field name and pattern.");
            }

            if (!SupportedSignatureFields.Contains(fieldName))
            {
                throw new InvalidOperationException($"Unsupported signatures field '{fieldName}'.");
            }

            var pointerOffsets = signature.PointerOffsets is null
                ? Array.Empty<long>()
                : signature.PointerOffsets.Select(value => ParseOffset(value, $"signatures.{fieldName}.pointer_offsets")).ToArray();
            var pointerSize = signature.PointerSize ?? 4;
            if (pointerSize is not 4 and not 8)
            {
                throw new InvalidOperationException($"signatures.{fieldName}.pointer_size must be 4 or 8.");
            }

            result[fieldName] = new MemoryAddressSignature
            {
                Pattern = signature.Pattern.Trim(),
                AddressMode = ParseAddressMode(signature.AddressMode, fieldName),
                OperandOffset = signature.OperandOffset ?? 0,
                MatchOffset = signature.MatchOffset ?? 0,
                AddressOffset = string.IsNullOrWhiteSpace(signature.AddressOffset)
                    ? 0
                    : ParseOffset(signature.AddressOffset, $"signatures.{fieldName}.address_offset"),
                PointerOffsets = pointerOffsets,
                PointerSize = pointerSize
            };
        }

        return result;
    }

    private static MemorySignatureAddressMode ParseAddressMode(string? value, string fieldName)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            "match" => MemorySignatureAddressMode.Match,
            "absolute32" or null or "" => MemorySignatureAddressMode.Absolute32,
            "absolute64" => MemorySignatureAddressMode.Absolute64,
            "relative32" => MemorySignatureAddressMode.Relative32,
            _ => throw new InvalidOperationException($"Unsupported signatures.{fieldName}.address_mode '{value}'.")
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

        public Dictionary<string, SignatureYaml>? Signatures { get; init; }
    }

    private sealed class SignatureYaml
    {
        public string? Pattern { get; init; }

        public string? AddressMode { get; init; }

        public int? OperandOffset { get; init; }

        public int? MatchOffset { get; init; }

        public string? AddressOffset { get; init; }

        public List<string>? PointerOffsets { get; init; }

        public int? PointerSize { get; init; }
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
