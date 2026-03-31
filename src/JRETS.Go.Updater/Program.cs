﻿using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

var exitCode = await RunAsync(args).ConfigureAwait(false);
Environment.Exit(exitCode);

static async Task<int> RunAsync(string[] args)
{
	var logger = UpdaterLogger.CreateDefault();

	try
	{
		logger.Info("Updater started.");
		var parsed = ParseArgs(args);
		logger.Info($"Parsed args. appDir={parsed.AppDirectory}, package={parsed.AppPackagePath}");

		await WaitForProcessExitAsync(parsed.ParentProcessId, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
		logger.Info($"Parent process exited. pid={parsed.ParentProcessId}");

		var extractDirectory = Path.Combine(Path.GetTempPath(), "JRETS.Go.App", "updater-extract", Guid.NewGuid().ToString("N"));
		var backupDirectory = Path.Combine(Path.GetTempPath(), "JRETS.Go.App", "updater-backup", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(extractDirectory);
		Directory.CreateDirectory(backupDirectory);

		try
		{
			logger.Info("Extracting package.");
			ZipFile.ExtractToDirectory(parsed.AppPackagePath, extractDirectory, overwriteFiles: true);

			logger.Info("Backing up current app files.");
			BackupCurrentApp(parsed.AppDirectory, backupDirectory);

			logger.Info("Applying package files.");
			ApplyPackage(extractDirectory, parsed.AppDirectory);

			logger.Info("Writing updated channel state.");
			WriteStateFile(parsed);
		}
		catch (Exception ex)
		{
			logger.Error($"Apply failed: {ex.Message}");
			logger.Info("Attempting rollback from backup.");
			TryRestoreBackup(backupDirectory, parsed.AppDirectory, logger);
			throw;
		}
		finally
		{
			TryDeleteDirectory(extractDirectory);
			TryDeleteDirectory(backupDirectory);
		}

		logger.Info("Restarting main executable.");
		StartMainExecutable(parsed.MainExecutablePath);
		logger.Info("Updater finished successfully.");
		return 0;
	}
	catch (Exception ex)
	{
		logger.Error($"Updater failed: {ex}");
		return 1;
	}
}

static ParsedArguments ParseArgs(string[] args)
{
	if (args.Length % 2 != 0)
	{
		throw new InvalidOperationException("Updater arguments must be provided as key/value pairs.");
	}

	var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
	for (var i = 0; i < args.Length - 1; i += 2)
	{
		map[args[i]] = args[i + 1];
	}

	return new ParsedArguments
	{
		AppDirectory = Require(map, "--app-dir"),
		AppPackagePath = Require(map, "--package"),
		MainExecutablePath = Require(map, "--main-exe"),
		ParentProcessId = int.Parse(Require(map, "--pid")),
		StateFilePath = Require(map, "--state-path"),
		TargetAppVersion = Require(map, "--target-app-version"),
		TargetConfigsVersion = Require(map, "--target-configs-version"),
		TargetAudioVersion = Require(map, "--target-audio-version")
	};
}

static string Require(IReadOnlyDictionary<string, string> map, string key)
{
    if (map.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    throw new InvalidOperationException($"Required updater argument is missing: {key}");
}

static async Task WaitForProcessExitAsync(int processId, TimeSpan timeout)
{
	Process? process;
	try
	{
		process = Process.GetProcessById(processId);
	}
	catch
	{
		return;
	}

	using (process)
	{
		if (process.HasExited)
		{
			return;
		}

		using var cts = new CancellationTokenSource(timeout);
		while (!process.HasExited && !cts.Token.IsCancellationRequested)
		{
			await Task.Delay(250, cts.Token).ConfigureAwait(false);
		}
	}
}

static void BackupCurrentApp(string appDirectory, string backupDirectory)
{
	foreach (var sourceFile in Directory.EnumerateFiles(appDirectory, "*", SearchOption.AllDirectories))
	{
		var relative = Path.GetRelativePath(appDirectory, sourceFile);
		var targetFile = Path.Combine(backupDirectory, relative);
		var targetDirectory = Path.GetDirectoryName(targetFile)
			?? throw new InvalidOperationException("Backup target directory could not be resolved.");

		Directory.CreateDirectory(targetDirectory);
		File.Copy(sourceFile, targetFile, overwrite: true);
	}
}

static void ApplyPackage(string extractDirectory, string appDirectory)
{
	var extractedRoot = Directory.Exists(Path.Combine(extractDirectory, "app"))
		? Path.Combine(extractDirectory, "app")
		: extractDirectory;

	foreach (var sourceFile in Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories))
	{
		var relative = Path.GetRelativePath(extractedRoot, sourceFile);
		if (relative.StartsWith("updater" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			// Skip updater files during self-update to avoid lock contention.
			continue;
		}

		var targetFile = Path.Combine(appDirectory, relative);
		var targetDirectory = Path.GetDirectoryName(targetFile)
			?? throw new InvalidOperationException("Target directory could not be resolved.");

		Directory.CreateDirectory(targetDirectory);
		File.Copy(sourceFile, targetFile, overwrite: true);
	}
}

static void TryRestoreBackup(string backupDirectory, string appDirectory, UpdaterLogger logger)
{
	try
	{
		foreach (var backupFile in Directory.EnumerateFiles(backupDirectory, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(backupDirectory, backupFile);
			if (relative.StartsWith("updater" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			var targetFile = Path.Combine(appDirectory, relative);
			var targetDirectory = Path.GetDirectoryName(targetFile)
				?? throw new InvalidOperationException("Rollback target directory could not be resolved.");

			Directory.CreateDirectory(targetDirectory);
			File.Copy(backupFile, targetFile, overwrite: true);
		}

		logger.Info("Rollback completed.");
	}
	catch (Exception ex)
	{
		logger.Error($"Rollback failed: {ex.Message}");
	}
}

static void WriteStateFile(ParsedArguments parsed)
{
    var stateDirectory = Path.GetDirectoryName(parsed.StateFilePath)
        ?? throw new InvalidOperationException("State directory could not be resolved.");

    Directory.CreateDirectory(stateDirectory);

    var state = new UpdateState
    {
        AppVersion = parsed.TargetAppVersion,
        ConfigsVersion = parsed.TargetConfigsVersion,
        AudioPackVersion = parsed.TargetAudioVersion
    };

    var content = JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    });
    File.WriteAllText(parsed.StateFilePath, content);
}

static void StartMainExecutable(string mainExecutablePath)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = mainExecutablePath,
        UseShellExecute = false,
        WorkingDirectory = Path.GetDirectoryName(mainExecutablePath)
    };

    Process.Start(startInfo);
}

static void TryDeleteDirectory(string path)
{
	try
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
	}
	catch
	{
		// Cleanup best effort.
	}
}

file sealed class UpdaterLogger
{
	private readonly string _logFilePath;

	private UpdaterLogger(string logFilePath)
	{
		_logFilePath = logFilePath;
	}

	public static UpdaterLogger CreateDefault()
	{
		var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		var logDirectory = Path.Combine(localAppData, "JRETS.Go.App", "logs");
		Directory.CreateDirectory(logDirectory);
		var logFilePath = Path.Combine(logDirectory, "updater.log");
		return new UpdaterLogger(logFilePath);
	}

	public void Info(string message)
	{
		Write("INFO", message);
	}

	public void Error(string message)
	{
		Write("ERROR", message);
	}

	private void Write(string level, string message)
	{
		var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
		File.AppendAllText(_logFilePath, line + Environment.NewLine, Encoding.UTF8);
	}
}

file sealed class ParsedArguments
{
    public required string AppDirectory { get; init; }

    public required string AppPackagePath { get; init; }

    public required string MainExecutablePath { get; init; }

    public required int ParentProcessId { get; init; }

    public required string StateFilePath { get; init; }

    public required string TargetAppVersion { get; init; }

    public required string TargetConfigsVersion { get; init; }

    public required string TargetAudioVersion { get; init; }
}

file sealed class UpdateState
{
    public required string AppVersion { get; init; }

    public required string ConfigsVersion { get; init; }

    public required string AudioPackVersion { get; init; }
}
