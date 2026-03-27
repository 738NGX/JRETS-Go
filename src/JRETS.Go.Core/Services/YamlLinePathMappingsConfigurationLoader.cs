using JRETS.Go.Core.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JRETS.Go.Core.Services;

public sealed class YamlLinePathMappingsConfigurationLoader : ILinePathMappingsConfigurationLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public LinePathMappingsConfiguration LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Line-path mappings config path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Line-path mappings config file was not found.", filePath);
        }

        var content = File.ReadAllText(filePath);
        var yaml = _deserializer.Deserialize<LinePathMappingsYaml>(content)
            ?? throw new InvalidOperationException("Line-path mappings config file is empty.");

        var mappings = new List<LinePathMappingEntry>();
        if (yaml.Paths is not null)
        {
            foreach (var pathMap in yaml.Paths)
            {
                if (pathMap is null)
                {
                    continue;
                }

                foreach (var pair in pathMap)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                    {
                        continue;
                    }

                    foreach (var value in pair.Value)
                    {
                        if (value is null
                            || string.IsNullOrWhiteSpace(value.LineId)
                            || string.IsNullOrWhiteSpace(value.TrainId))
                        {
                            continue;
                        }

                        mappings.Add(new LinePathMappingEntry
                        {
                            Path = pair.Key.Trim(),
                            LineId = value.LineId.Trim(),
                            TrainId = value.TrainId.Trim(),
                            DiagramId = string.IsNullOrWhiteSpace(value.DiagramId)
                                ? null
                                : value.DiagramId.Trim()
                        });
                    }
                }
            }
        }

        return new LinePathMappingsConfiguration
        {
            Paths = mappings
        };
    }

    private sealed class LinePathMappingsYaml
    {
        public List<Dictionary<string, List<LinePathMappingYaml>?>>? Paths { get; init; }
    }

    private sealed class LinePathMappingYaml
    {
        public string? LineId { get; init; }

        public string? TrainId { get; init; }

        public string? DiagramId { get; init; }
    }
}
