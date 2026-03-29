using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using JRETS.Go.App.Interop;

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
    }

    private void OnClosed(object? sender, EventArgs e)
    {
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
