using System.IO;
using System.Text.Json;

namespace JRETS.Go.App.Services;

public sealed class LocalReleaseStateStore
{
    private const string VendorDirectoryName = "JRETS.Go.App";
    private const string StateFileName = "update-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public LocalReleaseState Load(string currentAppVersion)
    {
        var statePath = GetStateFilePath();
        if (!File.Exists(statePath))
        {
            return new LocalReleaseState
            {
                AppVersion = NormalizeVersionOrDefault(currentAppVersion),
                ConfigsVersion = "0.0.0",
                AudioPackVersion = "0.0.0"
            };
        }

        try
        {
            var content = File.ReadAllText(statePath);
            var state = JsonSerializer.Deserialize<LocalReleaseState>(content, JsonOptions);
            if (state is null)
            {
                throw new InvalidOperationException("State file is empty.");
            }

            return new LocalReleaseState
            {
                AppVersion = NormalizeVersionOrDefault(state.AppVersion),
                ConfigsVersion = NormalizeVersionOrDefault(state.ConfigsVersion),
                AudioPackVersion = NormalizeVersionOrDefault(state.AudioPackVersion)
            };
        }
        catch
        {
            return new LocalReleaseState
            {
                AppVersion = NormalizeVersionOrDefault(currentAppVersion),
                ConfigsVersion = "0.0.0",
                AudioPackVersion = "0.0.0"
            };
        }
    }

    public void Save(LocalReleaseState state)
    {
        var statePath = GetStateFilePath();
        var stateDirectory = Path.GetDirectoryName(statePath)
            ?? throw new InvalidOperationException("State directory could not be resolved.");

        Directory.CreateDirectory(stateDirectory);
        var content = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(statePath, content);
    }

    public string GetStateFilePath()
    {
        return GetStateFilePathCore();
    }

    private static string GetStateFilePathCore()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, VendorDirectoryName, StateFileName);
    }

    private static string NormalizeVersionOrDefault(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        return value.Trim();
    }
}
