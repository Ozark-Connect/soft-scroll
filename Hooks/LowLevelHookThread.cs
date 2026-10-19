using System;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;
using SoftScroll.Native;

namespace SoftScroll.Hooks;

/// <summary>
/// Runs a WH_MOUSE_LL / WH_KEYBOARD_LL hook on its own thread with its own message loop.
/// Windows silently removes a low-level hook whose callback exceeds LowLevelHooksTimeout,
/// so the hook must not share a thread with WPF layout, dialogs or other slow UI work.
/// </summary>
internal sealed class LowLevelHookThread : IDisposable
{
    private readonly int _hookType;
    private readonly string _name;
    private readonly NativeMethods.HookProc _proc;
    private Thread? _thread;
    private uint _threadId;
    private volatile IntPtr _hook = IntPtr.Zero;

    public LowLevelHookThread(int hookType, string name, NativeMethods.HookProc proc)
    {
        _hookType = hookType;
        _name = name;
        // Field reference keeps the delegate alive for as long as the hook exists
        _proc = proc;
    }

    public IntPtr Handle => _hook;
    public bool IsInstalled => _hook != IntPtr.Zero;

    public void Install()
    {
        if (_thread != null) return;

        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = _name,
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
        ready.Wait();

        if (_hook == IntPtr.Zero)
        {
            _thread.Join(1000);
            _thread = null;
        }
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        // Local copy: a thread that outlives a timed-out Uninstall must not clear a newer thread's handle
        var hook = NativeMethods.SetWindowsHookEx(_hookType, _proc, NativeMethods.GetModuleHandle(null), 0);
        _hook = hook;
        if (hook == IntPtr.Zero)
            Log.Error("[{Name}] SetWindowsHookEx failed: {Error}", _name, Marshal.GetLastWin32Error());
        ready.Set();
        if (hook == IntPtr.Zero) return;

        // Hook callbacks are dispatched from inside GetMessage on this thread
        while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }

        NativeMethods.UnhookWindowsHookEx(hook);
        if (_hook == hook)
            _hook = IntPtr.Zero;
    }

    public void Uninstall()
    {
        var thread = _thread;
        if (thread == null) return;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        if (!thread.Join(1000))
            Log.Warning("[{Name}] Hook thread did not exit within 1 s", _name);
        _hook = IntPtr.Zero;
        _thread = null;
    }

    public void Dispose() => Uninstall();
}
