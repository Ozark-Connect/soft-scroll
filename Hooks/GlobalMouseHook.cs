using System;
using SoftScroll.Native;

namespace SoftScroll.Hooks;

public sealed class MouseWheelEventArgs : EventArgs
{
    public int Delta { get; }
    public bool Handled { get; set; }
    public MouseWheelEventArgs(int delta) => Delta = delta;
}

public sealed class MousePositionEventArgs : EventArgs
{
    public int X { get; }
    public int Y { get; }
    public bool Handled { get; set; }
    public MousePositionEventArgs(int x, int y) { X = x; Y = y; }
}

// Low-level mouse hook with ability to mark wheel events handled.
// Events are raised on the hook thread, not the UI thread.
public sealed class GlobalMouseHook : IDisposable
{
    private readonly LowLevelHookThread _thread;

    private readonly KeyboardStateSampler _keyboard = new();

    // Track middle-click state to avoid firing MouseMoved on every normal mouse move
    private volatile bool _middleClickActive;
    // A swallowed middle-button down must have its matching up swallowed too
    private bool _middleDownSwallowed;

    public bool IsInstalled => _thread.IsInstalled;

    /// <summary>
    /// When true, holding Shift will convert vertical scroll to horizontal.
    /// </summary>
    public bool ShiftKeyHorizontal { get; set; } = true;

    public event EventHandler<MouseWheelEventArgs>? MouseWheel;
    /// <summary>Horizontal wheel in WM_MOUSEHWHEEL convention (positive = right). Sender's
    /// <see cref="LastHWheelFromShift"/> tells whether it came from Shift + vertical wheel.</summary>
    public event EventHandler<MouseWheelEventArgs>? MouseHWheel;
    public event EventHandler<MouseWheelEventArgs>? MouseZoomWheel;
    public event EventHandler<MousePositionEventArgs>? MiddleButtonDown;
    public event EventHandler<MousePositionEventArgs>? MiddleButtonUp;
    public event EventHandler<MousePositionEventArgs>? MouseMoved;

    /// <summary>True while a MouseHWheel event raised from Shift + vertical wheel is being handled.</summary>
    public bool LastHWheelFromShift { get; private set; }

    public GlobalMouseHook()
    {
        _thread = new LowLevelHookThread(NativeMethods.WH_MOUSE_LL, "MouseHook", HookCallback);
    }

    public void Install()
    {
        if (IsInstalled) return;
        _keyboard.Start();
        _thread.Install();
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        _thread.Uninstall();
        _keyboard.Stop();
        _middleClickActive = false;
        _middleDownSwallowed = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var data = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);

            // Skip only our own output; wheel events injected by other software
            // (vendor mouse tools, remappers) are still smoothed
            if (data.dwExtraInfo == NativeMethods.InjectionSignature)
                return NativeMethods.CallNextHookEx(_thread.Handle, nCode, wParam, lParam);

            if (msg == NativeMethods.WM_MOUSEWHEEL)
            {
                int delta = (short)((data.mouseData >> 16) & 0xffff);
                var args = new MouseWheelEventArgs(delta);

                if (_keyboard.IsCtrlPressed)
                {
                    MouseZoomWheel?.Invoke(this, args);
                }
                else if (ShiftKeyHorizontal && _keyboard.IsShiftPressed)
                {
                    // Convert to WM_MOUSEHWHEEL convention: Shift + wheel up (+) scrolls LEFT (-).
                    // The engine flips it back when it re-emits Shift + vertical wheel.
                    // See GitHub issue #13.
                    var hArgs = new MouseWheelEventArgs(-delta);
                    LastHWheelFromShift = true;
                    MouseHWheel?.Invoke(this, hArgs);
                    LastHWheelFromShift = false;
                    if (hArgs.Handled)
                        args.Handled = true;
                }
                else
                {
                    MouseWheel?.Invoke(this, args);
                }

                if (args.Handled)
                    return (IntPtr)1;
            }
            else if (msg == NativeMethods.WM_MOUSEHWHEEL)
            {
                int delta = (short)((data.mouseData >> 16) & 0xffff);
                var args = new MouseWheelEventArgs(delta);
                MouseHWheel?.Invoke(this, args);
                if (args.Handled)
                    return (IntPtr)1;
            }
            else if (msg == NativeMethods.WM_MBUTTONDOWN)
            {
                _middleClickActive = true;
                var args = new MousePositionEventArgs(data.pt.x, data.pt.y);
                MiddleButtonDown?.Invoke(this, args);
                _middleDownSwallowed = args.Handled;
                if (args.Handled)
                    return (IntPtr)1;
            }
            else if (msg == NativeMethods.WM_MBUTTONUP)
            {
                _middleClickActive = false;
                MiddleButtonUp?.Invoke(this, new MousePositionEventArgs(data.pt.x, data.pt.y));
                if (_middleDownSwallowed)
                {
                    _middleDownSwallowed = false;
                    return (IntPtr)1;
                }
            }
            else if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                // Only fire MouseMoved when middle-click scrolling is active
                // to avoid lag from hundreds of callbacks per second
                if (_middleClickActive)
                    MouseMoved?.Invoke(this, new MousePositionEventArgs(data.pt.x, data.pt.y));
            }
        }
        return NativeMethods.CallNextHookEx(_thread.Handle, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
