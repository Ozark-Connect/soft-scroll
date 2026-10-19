using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Serilog;
using SoftScroll.Native;

namespace SoftScroll.Core;

/// <summary>
/// Listens for Raw Input messages to detect which input device (touchpad vs mouse)
/// is currently being used. Also listens for device arrival/removal to re-enumerate devices.
/// Uses a hidden message-only window on the thread that calls <see cref="Initialize"/>.
/// </summary>
public sealed class RawInputListener : IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly InputDeviceDetector _deviceDetector;
    private IntPtr _hwnd;
    private bool _disposed;
    private bool _registered;
    private DispatcherTimer? _reenumerateTimer;

    // MUST keep delegate reference alive to prevent GC from collecting it
    private WndProcDelegate? _myWndProcDelegate;

    public event EventHandler? DevicesChanged;

    public RawInputListener(InputDeviceDetector deviceDetector)
    {
        _deviceDetector = deviceDetector ?? throw new ArgumentNullException(nameof(deviceDetector));
    }

    /// <summary>
    /// Creates the hidden window and registers for Raw Input. Safe to call more than once.
    /// Call on the UI thread.
    /// </summary>
    public void Initialize()
    {
        if (_registered || _disposed)
            return;

        try
        {
            if (_hwnd == IntPtr.Zero)
                _hwnd = CreateHiddenWindow();
            if (_hwnd == IntPtr.Zero)
            {
                Log.Warning("[RawInput] Failed to create hidden window");
                return;
            }

            // Device arrival floods in right after registration (one per device); coalesce re-enumeration
            _reenumerateTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, OnReenumerate, Dispatcher.CurrentDispatcher) { IsEnabled = false };

            if (!RegisterRawInputDevices())
            {
                Log.Warning("[RawInput] Failed to register Raw Input devices");
            }
            else
            {
                _registered = true;
                Log.Information("[RawInput] Registered for Raw Input");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[RawInput] Failed to initialize Raw Input listener");
        }
    }

    private IntPtr CreateHiddenWindow()
    {
        // CRITICAL: Keep delegate alive to prevent GC
        _myWndProcDelegate = MyWndProc;

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _myWndProcDelegate,
            hInstance = GetModuleHandle(null),
            lpszClassName = "SoftScroll_RawInput_Window"
        };

        uint classAtom = RegisterClassEx(ref wc);
        if (classAtom == 0)
        {
            Log.Warning("[RawInput] RegisterClassEx failed: {Error}", Marshal.GetLastWin32Error());
            return IntPtr.Zero;
        }

        // Create message-only window
        var hwnd = CreateWindowEx(
            0,
            new IntPtr(classAtom),
            "SoftScroll RawInput",
            0,
            0, 0, 0, 0,
            HWND_MESSAGE,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            Log.Warning("[RawInput] CreateWindowEx failed: {Error}", Marshal.GetLastWin32Error());
        }

        return hwnd;
    }

    private bool RegisterRawInputDevices()
    {
        // Mice (including a touchpad's mouse collection) and Precision Touchpad digitizer reports.
        // INPUTSINK: receive input while another app has focus. DEVNOTIFY: WM_INPUT_DEVICE_CHANGE.
        const uint flags = NativeMethods.RIDEV_INPUTSINK | NativeMethods.RIDEV_DEVNOTIFY;
        var rid = new NativeMethods.RAWINPUTDEVICE[]
        {
            new() { usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC, usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE, dwFlags = flags, hwndTarget = _hwnd },
            new() { usUsagePage = NativeMethods.HID_USAGE_PAGE_DIGITIZER, usUsage = NativeMethods.HID_USAGE_DIGITIZER_TOUCH_PAD, dwFlags = flags, hwndTarget = _hwnd },
        };

        bool result = NativeMethods.RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>());
        if (!result)
        {
            Log.Warning("[RawInput] RegisterRawInputDevices failed: {Error}", Marshal.GetLastWin32Error());
            return false;
        }

        return true;
    }

    private IntPtr MyWndProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_INPUT)
        {
            ProcessRawInput(lParam);
            // DefWindowProc performs the required cleanup for WM_INPUT
        }
        else if (msg == NativeMethods.WM_INPUT_DEVICE_CHANGE)
        {
            _reenumerateTimer?.Stop();
            _reenumerateTimer?.Start();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ProcessRawInput(IntPtr lParam)
    {
        try
        {
            // The header alone identifies the device; no need to copy the report
            uint headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
            uint size = headerSize;
            IntPtr data = GetOrCreateBuffer((int)headerSize);
            if (data == IntPtr.Zero) return;

            uint result = NativeMethods.GetRawInputData(lParam, NativeMethods.RID_HEADER, data, ref size, headerSize);
            if (result == unchecked((uint)-1) || result == 0)
                return;

            var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(data);
            if (header.dwType == NativeMethods.RIM_TYPEMOUSE || header.dwType == NativeMethods.RIM_TYPEHID)
                _deviceDetector.ProcessRawInput(header.hDevice);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RawInput] Error processing raw input");
        }
    }

    // Reusable buffer to avoid Marshal.AllocHGlobal on every call
    private IntPtr _buffer;
    private int _bufferSize;

    private IntPtr GetOrCreateBuffer(int size)
    {
        if (_buffer == IntPtr.Zero || size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(_buffer);
            _buffer = Marshal.AllocHGlobal(size);
            _bufferSize = size;
        }
        return _buffer;
    }

    private void OnReenumerate(object? sender, EventArgs e)
    {
        _reenumerateTimer?.Stop();
        Log.Information("[RawInput] Input devices changed, re-enumerating");
        try
        {
            _deviceDetector.EnumerateDevices();
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RawInput] Error re-enumerating devices");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            _reenumerateTimer?.Stop();

            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }

            // Free the reusable buffer
            if (_buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _bufferSize = 0;
            }

            // Release delegate so window class can be cleaned up
            _myWndProcDelegate = null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RawInput] Error during disposal");
        }

        GC.SuppressFinalize(this);
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    // Unicode throughout: the WNDCLASSEX strings are UTF-16, so the W entry points must be used
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClassEx(ref WNDCLASSEX lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        IntPtr lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate? lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }
}
