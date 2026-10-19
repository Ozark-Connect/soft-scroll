using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SoftScroll.Settings;

namespace SoftScroll.UI;

public partial class ScrollIndicator : Window
{
    private const double EdgeMargin = 20;

    private DispatcherTimer? _hideTimer;
    private int _lastSpeed;
    private IndicatorPosition _position = IndicatorPosition.TopRight;

    public ScrollIndicator()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => OverlayWindowHelper.MakeNonInteractive(this);
        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _hideTimer.Tick += (s, e) =>
        {
            _hideTimer?.Stop();
            FadeOut();
        };
    }

    /// <summary>Applies the Visual Feedback settings: screen corner and how long the indicator stays.</summary>
    public void ApplySettings(IndicatorPosition position, int durationMs)
    {
        Dispatcher.Invoke(() =>
        {
            _position = position;
            if (_hideTimer != null)
                _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, durationMs));
        });
    }

    private void FadeOut()
    {
        Dispatcher.Invoke(() =>
        {
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(300));
            fade.Completed += (s, e) => Hide();
            BeginAnimation(OpacityProperty, fade);
        });
    }

    /// <summary>
    /// Shows the indicator at the configured position on the monitor that contains the cursor.
    /// </summary>
    /// <param name="screenX">Cursor X in pixels.</param>
    /// <param name="screenY">Cursor Y in pixels.</param>
    public void ShowAt(int screenX, int screenY, int speed)
    {
        Dispatcher.Invoke(() =>
        {
            var area = OverlayWindowHelper.WorkAreaDips(this, screenX, screenY);
            PlaceInArea(area);

            // Update speed text
            _lastSpeed = Math.Abs(speed);
            SpeedText.Text = _lastSpeed.ToString();

            // Make visible with fade-in, without taking focus from the scrolled app
            Opacity = 0;
            Show();

            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
            BeginAnimation(OpacityProperty, fade);

            // Reset hide timer
            _hideTimer?.Stop();
            _hideTimer?.Start();
        });
    }

    public void UpdateSpeed(int speed)
    {
        Dispatcher.Invoke(() =>
        {
            _lastSpeed = Math.Abs(speed);
            SpeedText.Text = _lastSpeed.ToString();

            // Reset hide timer
            _hideTimer?.Stop();
            _hideTimer?.Start();
        });
    }

    private void PlaceInArea(Rect area)
    {
        switch (_position)
        {
            case IndicatorPosition.TopLeft:
                Left = area.Left + EdgeMargin;
                Top = area.Top + EdgeMargin;
                break;
            case IndicatorPosition.BottomLeft:
                Left = area.Left + EdgeMargin;
                Top = area.Bottom - Height - EdgeMargin;
                break;
            case IndicatorPosition.BottomRight:
                Left = area.Right - Width - EdgeMargin;
                Top = area.Bottom - Height - EdgeMargin;
                break;
            case IndicatorPosition.Center:
                Left = area.Left + (area.Width - Width) / 2;
                Top = area.Top + (area.Height - Height) / 2;
                break;
            default: // TopRight
                Left = area.Right - Width - EdgeMargin;
                Top = area.Top + EdgeMargin;
                break;
        }
    }
}
