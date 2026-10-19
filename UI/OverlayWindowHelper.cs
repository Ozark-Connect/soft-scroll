using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SoftScroll.Native;

namespace SoftScroll.UI;

/// <summary>
/// Shared behavior for the cursor overlays: never take focus, never receive mouse input,
/// and convert screen pixels to WPF device-independent units.
/// </summary>
internal static class OverlayWindowHelper
{
    /// <summary>
    /// Call from SourceInitialized. Without these styles the overlay activates on Show,
    /// steals the foreground from the app being scrolled, and catches injected wheel events.
    /// </summary>
    public static void MakeNonInteractive(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
    }

    /// <summary>Converts a screen point in pixels (GetCursorPos) to WPF units for Left/Top.</summary>
    public static System.Windows.Point PixelsToDips(Visual visual, double x, double y)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return new System.Windows.Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    /// <summary>Work area of the monitor that contains the given pixel point, in WPF units.</summary>
    public static Rect WorkAreaDips(Visual visual, int x, int y)
    {
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).WorkingArea;
        var topLeft = PixelsToDips(visual, area.Left, area.Top);
        var bottomRight = PixelsToDips(visual, area.Right, area.Bottom);
        return new Rect(topLeft, bottomRight);
    }
}
