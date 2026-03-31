using System.Diagnostics;
using System.IO;
using System.Text;
using JRETS.Go.Core.Configuration;

namespace JRETS.Go.App.Services;

public sealed class AppUpdateHandoffService
{
    public bool TryLaunchUpdater(
        string appBaseDirectory,
        UpdateCheckResult checkResult,
        StagedUpdateAssets stagedAssets,
        LocalReleaseState nextState,
        string stateFilePath,
        out string errorMessage)
    {
        errorMessage = string.Empty;

        var appAsset = stagedAssets.AppAsset;
        if (appAsset is null)
        {
            errorMessage = "App asset is not staged.";
            return false;
        }

        var updaterExePath = Path.Combine(appBaseDirectory, "updater", "JRETS.Go.Updater.exe");
        if (!File.Exists(updaterExePath))
        {
            errorMessage = $"Updater executable not found: {updaterExePath}";
            return false;
        }

        var currentProcess = Process.GetCurrentProcess();
        var mainExePath = currentProcess.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(mainExePath))
        {
            errorMessage = "Current main executable path could not be resolved.";
            return false;
        }

        var arguments = BuildArguments(
            appBaseDirectory,
            appAsset.FilePath,
            mainExePath,
            currentProcess.Id,
            stateFilePath,
            checkResult.TargetState,
            nextState.ConfigsVersion,
            nextState.AudioPackVersion);

        var startInfo = new ProcessStartInfo
        {
            FileName = updaterExePath,
            Arguments = arguments,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(updaterExePath) ?? appBaseDirectory,
            CreateNoWindow = true
        };

        try
        {
            var started = Process.Start(startInfo);
            if (started is null)
            {
                errorMessage = "Updater process did not start.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to launch updater: {ex.Message}";
            return false;
        }
    }

    private static string BuildArguments(
        string appDirectory,
        string appPackagePath,
        string mainExePath,
        int parentProcessId,
        string stateFilePath,
        ReleaseStateConfiguration targetState,
        string currentConfigsVersion,
        string currentAudioVersion)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, "--app-dir", appDirectory);
        AppendArgument(builder, "--package", appPackagePath);
        AppendArgument(builder, "--main-exe", mainExePath);
        AppendArgument(builder, "--pid", parentProcessId.ToString());
        AppendArgument(builder, "--state-path", stateFilePath);
        AppendArgument(builder, "--target-app-version", targetState.AppVersion);
        AppendArgument(builder, "--target-configs-version", currentConfigsVersion);
        AppendArgument(builder, "--target-audio-version", currentAudioVersion);
        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string key, string value)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        builder.Append(key);
        builder.Append(' ');
        builder.Append('"');
        builder.Append(value.Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
    }
}
