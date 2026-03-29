using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace JRETS.Go.App;

/// <summary>
/// Partial class for mini-map panel animation and availability messaging.
/// Handles slide/opacity animations, panel visibility toggling, and map data availability indicators.
/// </summary>
public partial class MainWindow
{
    private void AnimateMiniMapPanel(bool show)
    {
        _isMiniMapPanelVisible = show;
        MiniMapPanel.Visibility = Visibility.Visible;

        MiniMapPanelTransform.BeginAnimation(TranslateTransform.XProperty, null);
        MiniMapPanel.BeginAnimation(OpacityProperty, null);

        MiniMapPanelTransform.X = show ? MiniMapPanelHiddenOffsetX : MiniMapPanelVisibleOffsetX;
        MiniMapPanel.Opacity = show ? 0 : 1;

        var slideAnimation = new DoubleAnimation
        {
            From = MiniMapPanelTransform.X,
            To = show ? MiniMapPanelVisibleOffsetX : MiniMapPanelHiddenOffsetX,
            Duration = MiniMapPanelAnimationDuration,
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        var opacityAnimation = new DoubleAnimation
        {
            From = MiniMapPanel.Opacity,
            To = show ? 1 : 0,
            Duration = MiniMapPanelAnimationDuration
        };

        if (!show)
        {
            opacityAnimation.Completed += (_, _) =>
            {
                if (!_isMiniMapPanelVisible)
                {
                    MiniMapPanel.Visibility = Visibility.Collapsed;
                }
            };
        }

        MiniMapPanelTransform.BeginAnimation(TranslateTransform.XProperty, slideAnimation);
        MiniMapPanel.BeginAnimation(OpacityProperty, opacityAnimation);
    }

    private void ToggleMiniMapPanelVisibility()
    {
        if (_isMiniMapPanelVisible)
        {
            AnimateMiniMapPanel(show: false);
            return;
        }

        AnimateMiniMapPanel(show: true);

        if (!_sessionRunning)
        {
            DrawMapAvailabilityMessage("運転外です", "Not driving now");
            return;
        }

        if (!_miniMapDataAvailable)
        {
            DrawMapAvailabilityMessage("この路線の地図データがありません", "No map data for this line");
            return;
        }

        _ = RenderMapAsync(_latestApproachSnapshot, _latestApproachState, force: true);
    }

    private void DrawMapAvailabilityMessage(string japaneseMessage, string englishMessage)
    {
        ClearNativeMap();

        var width = MiniMapCanvas.ActualWidth > 1 ? MiniMapCanvas.ActualWidth : 480;
        var height = MiniMapCanvas.ActualHeight > 1 ? MiniMapCanvas.ActualHeight : 460;

        var hintBackground = new Border
        {
            Width = Math.Min(420, width - 20),
            Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 10, 14, 10)
        };

        var hintStack = new StackPanel
        {
            Orientation = Orientation.Vertical
        };

        hintStack.Children.Add(new TextBlock
        {
            Text = japaneseMessage,
            Foreground = Brushes.White,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        hintStack.Children.Add(new TextBlock
        {
            Text = englishMessage,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 226, 242)),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 6, 0, 0),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });

        hintBackground.Child = hintStack;

        hintBackground.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var desired = hintBackground.DesiredSize;
        var left = Math.Max(8, (width - desired.Width) / 2);
        var top = Math.Max(8, (height - desired.Height) / 2);

        Canvas.SetLeft(hintBackground, left);
        Canvas.SetTop(hintBackground, top);
        MiniMapCanvas.Children.Add(hintBackground);
    }
}
