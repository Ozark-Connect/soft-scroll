using System;
using System.Runtime.InteropServices;
using SoftScroll.Native;

namespace SoftScroll.Hooks;

public sealed class GlobalHotkey : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private readonly LowLevelHookThread _thread;
    private readonly int _id;
    private readonly uint _modifiers;
    private readonly ushort _key;
    private volatile bool _enabled = true;
    private bool _keyHeld;

    /// <summary>Raised on the hook thread, not the UI thread.</summary>
    public event EventHandler? HotkeyPressed;

    public GlobalHotkey(int id, uint modifiers, ushort key)
    {
        _id = id;
        _modifiers = modifiers;
        _key = key;
        _thread = new LowLevelHookThread(WH_KEYBOARD_LL, "HotkeyHook", HookCallback);
    }

    public void Install() => _thread.Install();

    public void Uninstall() => _thread.Uninstall();

    public void SetEnabled(bool enabled) => _enabled = enabled;

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            var msg = wParam.ToInt32();
            // lParam points to a KBDLLHOOKSTRUCT; vkCode is its first DWORD
            var vk = (ushort)Marshal.ReadInt32(lParam);

            if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && vk == _key)
            {
                _keyHeld = false;
            }
            else if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
            {
                bool ctrl = IsDown(NativeMethods.VK_CONTROL);
                bool shift = IsDown(NativeMethods.VK_SHIFT);
                bool alt = IsDown(NativeMethods.VK_MENU);
                bool win = IsDown(NativeMethods.VK_LWIN) || IsDown(NativeMethods.VK_RWIN);
                // AltGr reports as LCtrl + RAlt; AltGr+S types a character on many layouts
                bool altGr = IsDown(NativeMethods.VK_RMENU);

                bool modCtrl = (_modifiers & HotkeyConstants.MOD_CONTROL) != 0;
                bool modShift = (_modifiers & HotkeyConstants.MOD_SHIFT) != 0;
                bool modAlt = (_modifiers & HotkeyConstants.MOD_ALT) != 0;
                bool modWin = (_modifiers & HotkeyConstants.MOD_WIN) != 0;

                if (vk == _key &&
                    ctrl == modCtrl &&
                    shift == modShift &&
                    alt == modAlt &&
                    win == modWin &&
                    !(altGr && modCtrl && modAlt))
                {
                    // Key autorepeat sends repeated keydowns; MOD_NOREPEAT fires once per press
                    bool noRepeat = (_modifiers & HotkeyConstants.MOD_NOREPEAT) != 0;
                    if (!(noRepeat && _keyHeld))
                    {
                        _keyHeld = true;
                        HotkeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                    return (IntPtr)1;
                }
            }
        }
        return NativeMethods.CallNextHookEx(_thread.Handle, nCode, wParam, lParam);
    }

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose()
    {
        Uninstall();
    }
}

public static class HotkeyConstants
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    public const ushort VK_S = 0x53;
    public const ushort VK_F23 = 0xFC;
}
