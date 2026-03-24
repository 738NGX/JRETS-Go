using JRETS.Go.Core.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace JRETS.Go.Core.Services;

public sealed class YamlScoringConfigurationLoader : IScoringConfigurationLoader
{
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public ScoringConfiguration LoadFromFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Scoring config path is required.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Scoring config file was not found.", filePath);
        }

        var content = File.ReadAllText(filePath);
        var yaml = _deserializer.Deserialize<ScoringConfigurationYaml>(content)
            ?? throw new InvalidOperationException("Scoring config file is empty.");

        if (yaml.Scoring is null)
        {
            throw new InvalidOperationException("scoring section is required.");
        }

        var config = new ScoringConfiguration
        {
            PositionPenaltyPerMeter = yaml.Scoring.PositionPenaltyPerMeter,
            TimePenaltyPerSecond = yaml.Scoring.TimePenaltyPerSecond,
            PositionWeight = yaml.Scoring.PositionWeight,
            TimeWeight = yaml.Scoring.TimeWeight,
            MaxScorePerStop = yaml.Scoring.MaxScorePerStop
        };

        Validate(config);
        return config;
    }

    private static void Validate(ScoringConfiguration config)
    {
        if (config.PositionPenaltyPerMeter < 0 || config.TimePenaltyPerSecond < 0)
        {
            throw new InvalidOperationException("Penalty values must be non-negative.");
        }

        if (config.MaxScorePerStop <= 0)
        {
            throw new InvalidOperationException("max_score_per_stop must be greater than 0.");
        }

        var sum = config.PositionWeight + config.TimeWeight;
        if (Math.Abs(sum - 1.0) > 0.0001)
        {
            throw new InvalidOperationException("position_weight and time_weight must sum to 1.");
        }
    }

    private sealed class ScoringConfigurationYaml
    {
        public ScoringSection? Scoring { get; init; }
    }

    private sealed class ScoringSection
    {
        public double PositionPenaltyPerMeter { get; init; } = 2.0;

        public double TimePenaltyPerSecond { get; init; } = 1.0;

        public double PositionWeight { get; init; } = 0.6;

        public double TimeWeight { get; init; } = 0.4;

        public double MaxScorePerStop { get; init; } = 100.0;
    }
}
