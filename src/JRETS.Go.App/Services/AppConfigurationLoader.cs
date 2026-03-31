using System;
using System.Collections.Generic;
using System.IO;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Services;

namespace JRETS.Go.App.Services;

public sealed class AppConfigurationLoader
{
    public IReadOnlyList<LineConfigurationLoadResult> LoadLineConfigurations(string lineConfigsDirectory, string previousPath)
    {
        var lineLoader = new YamlLineConfigurationLoader();
        var results = new List<LineConfigurationLoadResult>();

        if (Directory.Exists(lineConfigsDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(lineConfigsDirectory, "*.yaml"))
            {
                try
                {
                    var config = lineLoader.LoadFromFile(file);
                    results.Add(new LineConfigurationLoadResult
                    {
                        FilePath = file,
                        Configuration = config,
                        DisplayName = $"{config.LineInfo.NameJp} ({Path.GetFileName(file)})"
                    });
                }
                catch
                {
                    // Ignore non-line yaml files.
                }
            }
        }

        if (results.Count == 0 && File.Exists(previousPath))
        {
            var fallbackConfig = lineLoader.LoadFromFile(previousPath);
            results.Add(new LineConfigurationLoadResult
            {
                FilePath = previousPath,
                Configuration = fallbackConfig,
                DisplayName = $"{fallbackConfig.LineInfo.NameJp} ({Path.GetFileName(previousPath)})"
            });
        }

        return results;
    }

    public Dictionary<string, LinePathMappingEntry> LoadLinePathMappings(
        string linePathMappingsConfigPath,
        Func<string?, string?> normalizeLinePath)
    {
        var result = new Dictionary<string, LinePathMappingEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(linePathMappingsConfigPath))
        {
            return result;
        }

        var loader = new YamlLinePathMappingsConfigurationLoader();
        var config = loader.LoadFromFile(linePathMappingsConfigPath);
        foreach (var entry in config.Paths)
        {
            if (string.IsNullOrWhiteSpace(entry.Path)
                || string.IsNullOrWhiteSpace(entry.LineId)
                || string.IsNullOrWhiteSpace(entry.TrainId))
            {
                continue;
            }

            var key = normalizeLinePath(entry.Path);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            result[key!] = entry;
        }

        return result;
    }

    public ProcessMemoryRealtimeDataSource? CreateMemoryDataSource(string offsetsConfigPath)
    {
        if (!File.Exists(offsetsConfigPath))
        {
            return null;
        }

        var offsetsLoader = new YamlMemoryOffsetsConfigurationLoader();
        var offsets = offsetsLoader.LoadFromFile(offsetsConfigPath);
        return new ProcessMemoryRealtimeDataSource(offsets);
    }

    public UpdateConfiguration LoadUpdateConfiguration(string updateConfigPath)
    {
        var updateLoader = new YamlUpdateConfigurationLoader();
        var config = updateLoader.LoadFromFile(updateConfigPath);

        ValidateUpdateConfiguration(config);
        return config;
    }

    public string ResolveConfigPath(string configsDirectory, string fileName)
    {
        return ResolveConfigPath(configsDirectory, fileName, fileName);
    }

    public string ResolveConfigPath(string configsDirectory, string sampleFileName, string preferredFileName)
    {
        var preferredPath = Path.Combine(configsDirectory, preferredFileName);
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        return Path.Combine(configsDirectory, sampleFileName);
    }

    public string ResolveLineConfigPath(string lineConfigsDirectory)
    {
        var preferredPath = Path.Combine(lineConfigsDirectory, "keihin-negishi.yaml");
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        if (!Directory.Exists(lineConfigsDirectory))
        {
            return preferredPath;
        }

        var loader = new YamlLineConfigurationLoader();
        foreach (var file in Directory.EnumerateFiles(lineConfigsDirectory, "*.yaml"))
        {
            try
            {
                loader.LoadFromFile(file);
                return file;
            }
            catch
            {
                // Ignore non-line yaml files.
            }
        }

        return preferredPath;
    }

    private static void ValidateUpdateConfiguration(UpdateConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.GitHub.Owner)
            || string.IsNullOrWhiteSpace(config.GitHub.Repo))
        {
            throw new InvalidOperationException("update.github.owner and update.github.repo are required.");
        }

        if (string.IsNullOrWhiteSpace(config.ReleaseStateAssetName))
        {
            throw new InvalidOperationException("update.release_state_asset_name is required.");
        }

        if (string.IsNullOrWhiteSpace(config.App.AssetPattern)
            || string.IsNullOrWhiteSpace(config.Configs.AssetPattern)
            || string.IsNullOrWhiteSpace(config.Audio.ManifestName)
            || string.IsNullOrWhiteSpace(config.Audio.PackagePattern))
        {
            throw new InvalidOperationException("update channels are missing required asset settings.");
        }

        if (config.CheckTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException("update.check_timeout_seconds must be greater than 0.");
        }

        if (config.MaxRetryCount < 0)
        {
            throw new InvalidOperationException("update.max_retry_count must be 0 or greater.");
        }
    }
}

public sealed class LineConfigurationLoadResult
{
    public required string DisplayName { get; init; }

    public required string FilePath { get; init; }

    public required LineConfiguration Configuration { get; init; }
}
