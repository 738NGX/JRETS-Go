using JRETS.Go.Core.Configuration;
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
            Stations = yaml.Stations.OrderBy(x => x.Id).ToArray()
        };
    }

    private sealed class LineConfigurationYaml
    {
        public LineInfo? LineInfo { get; init; }

        public List<TrainInfo>? TrainInfo { get; init; }

        public List<StationInfo>? Stations { get; init; }
    }
}
