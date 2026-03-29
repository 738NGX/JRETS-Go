using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using JRETS.Go.Core.Configuration;

namespace JRETS.Go.App;

public partial class MainWindow
{
    private void OpenMelodySelectionPanel(StationInfo? station)
    {
        if (station is null)
        {
            return;
        }

        _melodyCurrentStationId = station.Id;
        _melodyCurrentOptions = station.Melody is { Count: > 0 } ? new List<string>(station.Melody) : [];
        _melodySelectedIndex = 0;
        _melodyIsPlaying = false;

        if (_melodyCurrentOptions.Count == 0)
        {
            return;
        }

        UpdateMelodyPanelDisplay();
        AnimateMelodySelectionPanel(show: true);
    }

    private void CloseMelodySelectionPanel()
    {
        AnimateMelodySelectionPanel(show: false);
        _melodyCurrentStationId = null;
        _melodyCurrentOptions.Clear();
        _melodySelectedIndex = 0;
        StopMelodyPlayback();
    }

    private void AnimateMelodySelectionPanel(bool show)
    {
        _isMelodySelectionPanelVisible = show;
        MelodySelectionPanel.Visibility = Visibility.Visible;

        MelodySelectionPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
        MelodySelectionPanel.BeginAnimation(OpacityProperty, null);

        MelodySelectionPanelTransform.X = show ? MelodyPanelHiddenOffsetX : MelodyPanelVisibleOffsetX;
        MelodySelectionPanel.Opacity = show ? 0 : 1;

        var slideAnimation = new DoubleAnimation
        {
            From = MelodySelectionPanelTransform.X,
            To = show ? MelodyPanelVisibleOffsetX : MelodyPanelHiddenOffsetX,
            Duration = MelodyPanelAnimationDuration,
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        var opacityAnimation = new DoubleAnimation
        {
            From = MelodySelectionPanel.Opacity,
            To = show ? 1 : 0,
            Duration = MelodyPanelAnimationDuration
        };

        if (!show)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (!_isMelodySelectionPanelVisible)
                {
                    MelodySelectionPanel.Visibility = Visibility.Collapsed;
                }
            };
        }

        MelodySelectionPanelTransform.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
        MelodySelectionPanel.BeginAnimation(OpacityProperty, opacityAnimation);
    }

    private void CycleMelodySelection(bool reverse)
    {
        if (_melodyCurrentOptions.Count == 0)
        {
            return;
        }

        if (reverse)
        {
            _melodySelectedIndex = (_melodySelectedIndex - 1 + _melodyCurrentOptions.Count) % _melodyCurrentOptions.Count;
        }
        else
        {
            _melodySelectedIndex = (_melodySelectedIndex + 1) % _melodyCurrentOptions.Count;
        }

        StopMelodyPlayback();
        UpdateMelodyPanelDisplay();
    }

    private void ToggleMelodyPlayback()
    {
        if (_melodyCurrentOptions.Count == 0)
        {
            return;
        }

        if (_melodyIsPlaying)
        {
            StopMelodyPlayback();
        }
        else
        {
            PlayMelodyFile(_melodyCurrentOptions[_melodySelectedIndex]);
        }
    }

    private void PlayMelodyFile(string melodyFilename)
    {
        var rootPath = Path.Combine(AppContext.BaseDirectory, "audio", "melodies", melodyFilename);
        var lineId = _lineConfiguration?.LineInfo.Id;
        var lineScopedPath = string.IsNullOrWhiteSpace(lineId)
            ? string.Empty
            : Path.Combine(AppContext.BaseDirectory, "audio", "melodies", lineId, melodyFilename);

        var audioPath = File.Exists(rootPath)
            ? rootPath
            : lineScopedPath;

        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return;
        }

        try
        {
            var normalizedPath = _announcementAudioService.PrepareNormalizedAudio(
                audioPath,
                _announcementTempDirectory,
                _announcementNormalizedPathCache);
            _melodyPlaybackPlayer.Stop();
            _melodyPlaybackPlayer.Open(new Uri(normalizedPath, UriKind.Absolute));
            _melodyPlaybackPlayer.Volume = MelodyPlaybackVolume;
            _melodyPlaybackPlayer.Position = TimeSpan.Zero;
            _melodyPlaybackPlayer.Play();
            _melodyIsPlaying = true;
            UpdateMelodyPanelDisplay();
        }
        catch
        {
            // Silently fail if playback cannot start
        }
    }

    private void StopMelodyPlayback()
    {
        _melodyPlaybackPlayer.Stop();
        _melodyIsPlaying = false;
        UpdateMelodyPanelDisplay();
    }

    private void MelodyPlaybackPlayerOnMediaEnded(object? sender, EventArgs e)
    {
        if (!_melodyIsPlaying)
        {
            return;
        }

        if (_melodyCurrentOptions.Count == 0)
        {
            _melodyIsPlaying = false;
            UpdateMelodyPanelDisplay();
            return;
        }

        try
        {
            _melodyPlaybackPlayer.Position = TimeSpan.Zero;
            _melodyPlaybackPlayer.Play();
        }
        catch
        {
            _melodyIsPlaying = false;
            UpdateMelodyPanelDisplay();
        }
    }

    private void UpdateMelodyPanelDisplay()
    {
        if (_melodyCurrentOptions.Count == 0)
        {
            MelodyCurrentText.Text = "No melody";
            _melodyOptionItems.Clear();
            return;
        }

        var selectedIndex = Math.Clamp(_melodySelectedIndex, 0, _melodyCurrentOptions.Count - 1);
        var current = _melodyCurrentOptions[selectedIndex];
        var status = _melodyIsPlaying ? "▶ 再生中 / Playing" : "◻ 停止中 / Stopped";
        MelodyCurrentText.Text = $"{status}  ({selectedIndex + 1}/{_melodyCurrentOptions.Count})";

        _melodyOptionItems.Clear();
        for (var i = 0; i < _melodyCurrentOptions.Count; i++)
        {
            var isSelected = i == selectedIndex;
            _melodyOptionItems.Add(new MelodyOptionItem
            {
                Label = $"{i + 1:D2}. {_melodyCurrentOptions[i]}",
                Background = isSelected
                    ? new SolidColorBrush(Color.FromRgb(41, 100, 151))
                    : new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                Foreground = isSelected
                    ? new SolidColorBrush(Color.FromRgb(234, 244, 255))
                    : new SolidColorBrush(Color.FromRgb(167, 189, 212)),
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal
            });
        }
    }

    private sealed class MelodyOptionItem
    {
        public required string Label { get; init; }

        public required Brush Background { get; init; }

        public required Brush Foreground { get; init; }

        public required FontWeight FontWeight { get; init; }
    }

}
