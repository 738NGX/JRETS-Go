using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using JRETS.Go.App.Interop;
using JRETS.Go.App.Services;

namespace JRETS.Go.App;

public partial class MainWindow
{
    #region HotKey Registration and Handling

    private void RegisterGlobalHotKeys()
    {
        RegisterHotKey(HotKeyStartSession, HotKeyModifiers.NoRepeat, 0x78); // F9
        RegisterHotKey(HotKeyEndSession, HotKeyModifiers.NoRepeat, 0x79); // F10
        RegisterHotKey(HotKeyToggleClickThrough, HotKeyModifiers.NoRepeat, 0x74); // F5
        RegisterHotKey(HotKeyToggleReportWindow, HotKeyModifiers.NoRepeat, 0x75); // F6
        RegisterHotKey(HotKeyToggleMiniMapPanel, HotKeyModifiers.NoRepeat, 0x76); // F7
        RegisterHotKey(HotKeyToggleMap, HotKeyModifiers.NoRepeat, 0x77); // F8
        RegisterHotKey(HotKeyMelodyTogglePlayback, HotKeyModifiers.NoRepeat, 0x73); // F4
        RegisterHotKey(HotKeyMelodyCycleSelection, HotKeyModifiers.NoRepeat, 0x09); // Tab
    }

    private void RegisterHotKey(int id, HotKeyModifiers modifiers, uint virtualKey)
    {
        if (!NativeMethods.RegisterHotKey(_windowHandle, id, (uint)modifiers, virtualKey))
        {
            _lastDataSourceError =
                $"HotKey register failed for id={id}. Some shortcuts may be unavailable because another app already uses them.";
            return;
        }

        _registeredHotKeys.Add(id);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmHotKey)
        {
            return nint.Zero;
        }

        switch (wParam.ToInt32())
        {
            case HotKeyStartSession:
                StartSession();
                handled = true;
                break;
            case HotKeyEndSession:
                EndSession();
                handled = true;
                break;
            case HotKeyToggleClickThrough:
                ToggleClickThrough();
                handled = true;
                break;
            case HotKeyMelodyTogglePlayback:
                if (_isMelodySelectionPanelVisible)
                {
                    ToggleMelodyPlayback();
                }

                handled = true;
                break;
            case HotKeyMelodyCycleSelection:
                if (_isMelodySelectionPanelVisible)
                {
                    CycleMelodySelection(reverse: false);
                }

                handled = true;
                break;
            case HotKeyToggleReportWindow:
                ToggleReportWindow();
                handled = true;
                break;
            case HotKeyToggleMiniMapPanel:
                ToggleMiniMapPanelVisibility();
                handled = true;
                break;
            case HotKeyToggleMap:
                _ = ToggleMapBaseLayerAsync();
                handled = true;
                break;
        }

        return nint.Zero;
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Melody hotkeys are handled by global RegisterHotKey (WM_HOTKEY).
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // Melody hotkeys are handled by global RegisterHotKey (WM_HOTKEY).
    }

    #endregion

    #region UI Event Handlers

    private void StartSessionClick(object sender, RoutedEventArgs e)
    {
        StartSession();
    }

    private void EndSessionClick(object sender, RoutedEventArgs e)
    {
        EndSession();
    }

    private void DebugAdvanceClick(object sender, RoutedEventArgs e)
    {
        DebugAdvance();
    }

    private void DebugAdvance()
    {
        if (!_sessionRunning || _usingLiveMemory)
        {
            return;
        }

        _debugDataSource.DebugAdvance();
        UpdateDisplay();
    }

    private void ToggleClickThroughClick(object sender, RoutedEventArgs e)
    {
        ToggleClickThrough();
    }

    private void ToggleDebugModeClick(object sender, RoutedEventArgs e)
    {
        _debugModeEnabled = !_debugModeEnabled;
        UpdateDisplay();
    }

    private void ManualSelectionToggleChanged(object sender, RoutedEventArgs e)
    {
        _manualSelectionEnabled = EnableManualSelectionCheckBox.IsChecked == true;
        if (!_manualSelectionEnabled)
        {
            _hudStatusMessage = "手动线路选择已关闭：F9 时将自动识别线路与运行图。";
        }

        UpdateDisplay();
    }

    private void LineConfigSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboChange)
        {
            return;
        }

        if (LineConfigComboBox.SelectedItem is not LineConfigurationOption option)
        {
            return;
        }

        ApplyLineConfiguration(option);
        UpdateDisplay();
    }

    private void ServiceTypeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboChange)
        {
            return;
        }

        _selectedService = ServiceTypeComboBox.SelectedItem as TrainServiceOption;
        _timelineInitialized = false;
        UpdateDisplay();
    }

    private void ExitAppClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OpenLatestReportClick(object sender, RoutedEventArgs e)
    {
        OpenLatestReport();
    }

    #endregion

    #region Window Lifecycle

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        var interopHelper = new WindowInteropHelper(this);
        _windowHandle = interopHelper.Handle;

        var source = HwndSource.FromHwnd(_windowHandle);
        source?.AddHook(WndProc);

        RegisterGlobalHotKeys();
        StartStartupUpdateCheck();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _startupUpdateCheckCancellation?.Cancel();
        _startupUpdateCheckCancellation?.Dispose();
        _startupUpdateCheckCancellation = null;

        if (_windowHandle == nint.Zero)
        {
            return;
        }

        foreach (var hotKeyId in _registeredHotKeys)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, hotKeyId);
        }

        _configReloadDebounceTimer.Stop();
        _configWatcher.Dispose();
        _announcementPlayer.Close();
        _melodyPlaybackPlayer.Close();
        _announcementAudioService.TryCleanupTempDirectory(_announcementTempDirectory, TimeSpan.FromHours(12));
        StopLiveMemorySampling();
        _memoryDataSource?.Dispose();
        _reportViewerWindow?.Close();
    }

    private void StartStartupUpdateCheck()
    {
        _startupUpdateCheckCancellation?.Cancel();
        _startupUpdateCheckCancellation?.Dispose();
        _startupUpdateCheckCancellation = new CancellationTokenSource();

        _ = RunStartupUpdateCheckAsync(_startupUpdateCheckCancellation.Token);
    }

    private async Task RunStartupUpdateCheckAsync(CancellationToken cancellationToken)
    {
        var updateConfig = _updateConfiguration;
        if (updateConfig is null)
        {
            _mandatoryUpdatePending = false;
            _lastDataSourceError = string.IsNullOrWhiteSpace(_lastDataSourceError)
                ? "Update check skipped: update.yaml is missing or invalid."
                : _lastDataSourceError;
            return;
        }

        if (IsPlaceholderRepository(updateConfig))
        {
            _mandatoryUpdatePending = false;
            _lastDataSourceError = "Update check skipped: configure github.owner/repo in update.yaml.";
            return;
        }

        try
        {
            using var timeoutCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(updateConfig.CheckTimeoutSeconds));
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCancellation.Token);

            var localState = _localReleaseStateStore.Load(GetCurrentAppVersion());
            var checkResult = await _githubReleaseUpdateService
                .CheckForUpdatesAsync(updateConfig, localState, linkedCancellation.Token)
                .ConfigureAwait(false);

            var currentState = localState;
            StagedUpdateAssets? stagedAssets = null;
            if (checkResult.HasUpdate)
            {
                stagedAssets = await _releaseAssetStagingService
                    .DownloadAndVerifyAsync(checkResult, updateConfig.MaxRetryCount, linkedCancellation.Token)
                    .ConfigureAwait(false);

                currentState = await _channelUpdateApplyService
                    .ApplyNonAppChannelsAsync(
                        checkResult,
                        stagedAssets,
                        localState,
                        AppContext.BaseDirectory,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);

                _localReleaseStateStore.Save(currentState);
            }

            var unresolved = GetUnresolvedChannelsAfterApply(checkResult, currentState);
            var appPending = unresolved.Any(x => x.StartsWith("App ", StringComparison.OrdinalIgnoreCase));
            if (appPending && stagedAssets is not null)
            {
                var handoffStarted = _appUpdateHandoffService.TryLaunchUpdater(
                    AppContext.BaseDirectory,
                    checkResult,
                    stagedAssets,
                    currentState,
                    _localReleaseStateStore.GetStateFilePath(),
                    out var handoffError);

                if (handoffStarted)
                {
                    _mandatoryUpdatePending = true;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _lastDataSourceError = "Updater launched. App will exit for replacement.";
                        UpdateDisplay();
                        Application.Current.Shutdown();
                    });
                    return;
                }

                unresolved.Add($"App handoff failed: {handoffError}");
            }

            _mandatoryUpdatePending = updateConfig.Mandatory && unresolved.Count > 0;

            await Dispatcher.InvokeAsync(() =>
            {
                _lastDataSourceError = unresolved.Count > 0
                    ? $"Update pending: {string.Join(", ", unresolved)}"
                    : "Update check: all channels are up to date.";
                if (!_mandatoryUpdatePending && _hudStatusMessage == "检测到强制更新未完成，请先完成更新后再开始运行。")
                {
                    _hudStatusMessage = null;
                }
                UpdateDisplay();
            });
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
        catch (Exception ex)
        {
            _mandatoryUpdatePending = updateConfig.Mandatory;
            await Dispatcher.InvokeAsync(() =>
            {
                _lastDataSourceError = $"Update check failed: {ex.Message}";
                UpdateDisplay();
            });
        }
    }

    private static bool IsPlaceholderRepository(JRETS.Go.Core.Configuration.UpdateConfiguration updateConfig)
    {
        return string.Equals(updateConfig.GitHub.Owner, "your-org", StringComparison.OrdinalIgnoreCase)
            || string.Equals(updateConfig.GitHub.Repo, "your-repo", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCurrentAppVersion()
    {
        var version = typeof(MainWindow).Assembly.GetName().Version;
        if (version is null)
        {
            return "0.0.0";
        }

        var build = version.Build < 0 ? 0 : version.Build;
        return $"{version.Major}.{version.Minor}.{build}";
    }

    private static string FormatRequiredChannels(UpdateCheckResult result)
    {
        var required = result.Channels
            .Where(x => x.RequiresUpdate)
            .Select(x => $"{x.Channel} {x.LocalVersion} -> {x.TargetVersion}");
        return string.Join(", ", required);
    }

    private static List<string> GetUnresolvedChannelsAfterApply(UpdateCheckResult checkResult, LocalReleaseState state)
    {
        var unresolved = new List<string>();
        foreach (var channel in checkResult.Channels.Where(x => x.RequiresUpdate))
        {
            if (channel.Channel == UpdateChannel.App)
            {
                unresolved.Add($"App {state.AppVersion} -> {checkResult.TargetState.AppVersion}");
                continue;
            }

            if (channel.Channel == UpdateChannel.Configs
                && !string.Equals(state.ConfigsVersion, checkResult.TargetState.ConfigsVersion, StringComparison.OrdinalIgnoreCase))
            {
                unresolved.Add($"Configs {state.ConfigsVersion} -> {checkResult.TargetState.ConfigsVersion}");
            }

            if (channel.Channel == UpdateChannel.Audio
                && !string.Equals(state.AudioPackVersion, checkResult.TargetState.AudioPackVersion, StringComparison.OrdinalIgnoreCase))
            {
                unresolved.Add($"Audio {state.AudioPackVersion} -> {checkResult.TargetState.AudioPackVersion}");
            }
        }

        return unresolved;
    }

    #endregion

    #region Configuration Reload

    private void OnConfigChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _configReloadDebounceTimer.Stop();
            _configReloadDebounceTimer.Start();
        });
    }

    private void ApplyDebouncedConfigReload(object? sender, EventArgs e)
    {
        _configReloadDebounceTimer.Stop();

        try
        {
            ReloadConfigurations();
            _lastDataSourceError = "Config reloaded.";
        }
        catch (Exception ex)
        {
            _lastDataSourceError = $"Config reload failed: {ex.Message}";
        }

        UpdateDisplay();
    }

    #endregion
}
