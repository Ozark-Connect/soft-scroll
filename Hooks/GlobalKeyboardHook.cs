using System;
using System.Runtime.InteropServices;
using SoftScroll.Native;

namespace SoftScroll.Hooks;

/// <summary>
/// Low-level keyboard hook that reports key presses. It never swallows input.
/// Events are raised on the hook thread, not the UI thread.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly LowLevelHookThread _thread;
    // Hook thread only. Autorepeat sends repeated keydowns; only the first one of a press is reported.
    private readonly bool[] _held = new bool[256];

    public bool IsInstalled => _thread.IsInstalled;

    /// <summary>A key went down. Autorepeat keydowns are not reported.</summary>
    public event EventHandler? KeyPressed;

    public GlobalKeyboardHook()
    {
        _thread = new LowLevelHookThread(NativeMethods.WH_KEYBOARD_LL, "KeyboardHook", HookCallback);
    }

    public void Install()
    {
        if (IsInstalled) return;
        Array.Clear(_held);
        _thread.Install();
    }

    public void Uninstall()
    {
        if (!IsInstalled) return;
        _thread.Uninstall();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var msg = wParam.ToInt32();
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var vk = data.vkCode & 0xFF;

            // Skip our own output (zoom engine Ctrl presses)
            if (data.dwExtraInfo != NativeMethods.InjectionSignature)
            {
                if (msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN)
                {
                    if (!_held[vk])
                    {
                        _held[vk] = true;
                        KeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                }
                else if (msg == NativeMethods.WM_KEYUP || msg == NativeMethods.WM_SYSKEYUP)
                {
                    _held[vk] = false;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_thread.Handle, nCode, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
