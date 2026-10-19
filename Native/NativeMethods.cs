using System;
using System.Runtime.InteropServices;

namespace SoftScroll.Native;

/// <summary>
/// Centralized Win32 P/Invoke signatures and struct definitions.
/// Prevents duplication across engines, hooks, and helpers.
/// </summary>
internal static class NativeMethods
{
    // ── Mouse Hook ─────────────────────────────────────────────────

    internal const int WH_MOUSE_LL = 14;
    internal const int WM_MOUSEWHEEL = 0x020A;
    internal const int WM_MOUSEHWHEEL = 0x020E;
    internal const int WM_MBUTTONDOWN = 0x0207;
    internal const int WM_MBUTTONUP = 0x0208;
    internal const int WM_MOUSEMOVE = 0x0200;
    internal const int WM_INPUT = 0x00FF;

    internal const int LLMHF_INJECTED = 0x00000001;
    internal const int LLMHF_LOWER_IL_INJECTED = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public int mouseData;
        public int flags;
        public int time;
        public nint dwExtraInfo;
    }

    internal delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ── Message Loop (hook threads) ────────────────────────────────

    internal const int WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    internal static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();

    // ── Input Injection ────────────────────────────────────────────

    internal const int INPUT_MOUSE = 0;
    internal const int INPUT_KEYBOARD = 1;

    internal const int MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    internal const int MOUSEEVENTF_MIDDLEUP = 0x0040;
    internal const int MOUSEEVENTF_WHEEL = 0x0800;
    internal const int MOUSEEVENTF_HWHEEL = 0x01000;
    internal const int KEYEVENTF_KEYUP = 0x0002;
    internal const int KEYEVENTF_UNICODE = 0x0004;

    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_MENU = 0x12;
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_RWIN = 0x5C;
    internal const ushort VK_RMENU = 0xA5;

    // Tags every event this app injects (dwExtraInfo) so the mouse hook skips only
    // its own output and still smooths wheel events injected by other software
    internal static readonly nint InjectionSignature = 0x53465343; // "SFSC"

    internal static INPUT MouseInput(int flags, int mouseData = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags, mouseData = mouseData, dwExtraInfo = InjectionSignature } }
    };

    internal static INPUT KeyInput(ushort vk, int flags) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags, dwExtraInfo = InjectionSignature } }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT { public int type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal MOUSEINPUT mi;
        [FieldOffset(0)] internal KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public int dwFlags;
        public int time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public int dwFlags;
        public int time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll")]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    // ── Window / Process Queries ───────────────────────────────────

    internal const uint GA_ROOT = 2;
    internal const uint MK_SHIFT = 0x0004;
    internal const uint MK_CONTROL = 0x0008;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr PostMessageW(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TRANSPARENT = 0x00000020;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ── Timer Resolution ───────────────────────────────────────────

    [DllImport("winmm.dll", SetLastError = true)]
    internal static extern uint timeBeginPeriod(uint uMilliseconds);

    [DllImport("winmm.dll", SetLastError = true)]
    internal static extern uint timeEndPeriod(uint uMilliseconds);

    // ── Display Settings ───────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    internal struct DEVMODE
    {
        private const int CCHDEVICENAME = 32;
        private const int CCHFORMNAME = 32;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHFORMNAME)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Ansi)]
    internal static extern int EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    // Cached display refresh rate - detected lazily on first call
    private static int _cachedRefreshRate;
    private static bool _refreshRateCached;
    private static readonly object _refreshLock = new();

    internal static int GetDisplayRefreshRate()
    {
        if (_refreshRateCached) return _cachedRefreshRate;
        lock (_refreshLock)
        {
            if (_refreshRateCached) return _cachedRefreshRate;
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            int rate = 60; // fallback
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm) != 0 && dm.dmDisplayFrequency > 0)
                rate = dm.dmDisplayFrequency;
            _cachedRefreshRate = rate;
            _refreshRateCached = true;
            return rate;
        }
    }

    // ── Raw Input (Device Detection) ───────────────────────────────

    internal const int RIM_TYPEMOUSE = 0;
    internal const int RIM_TYPEKEYBOARD = 1;
    internal const int RIM_TYPEHID = 2;

    internal const int RIDI_DEVICENAME = 0x20000007;
    internal const int RIDI_DEVICEINFO = 0x2000000b;
    internal const int RID_INPUT = 0x10000003;
    internal const int RID_HEADER = 0x10000005;

    internal const uint RIDEV_INPUTSINK = 0x00000100;
    internal const uint RIDEV_DEVNOTIFY = 0x00002000;
    internal const int WM_INPUT_DEVICE_CHANGE = 0x00FE;

    // HID usages: Generic Desktop / Mouse, Digitizer / Touch Pad (Precision Touchpad)
    internal const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    internal const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    internal const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    internal const ushort HID_USAGE_DIGITIZER_TOUCH_PAD = 0x05;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUT
    {
        public RAWINPUTHEADER header;
        public RAWINPUTBODY data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct RAWINPUTBODY
    {
        [FieldOffset(0)] public RAWINPUTMOUSE mouse;
        [FieldOffset(0)] public RAWINPUTKEYBOARD keyboard;
        [FieldOffset(0)] public RAWINPUTHID hid;
    }

    // RAWMOUSE: usButtonFlags/usButtonData share a 4-byte-aligned union with ulButtons
    [StructLayout(LayoutKind.Explicit)]
    internal struct RAWINPUTMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public uint ulButtons;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTHID
    {
        public uint dwSizeHid;
        public uint dwCount;
        public IntPtr bRawData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RID_DEVICE_INFO_MOUSE
    {
        public uint dwId;
        public uint dwNumberOfButtons;
        public uint dwSampleRate;
        public uint fHasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId;
        public uint dwProductId;
        public uint dwVersionNumber;
        public ushort usUsagePage;
        public ushort usUsage;
    }

    // Size 24 = RID_DEVICE_INFO_KEYBOARD, the largest union member
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct RID_DEVICE_INFO_UNION
    {
        [FieldOffset(0)] public RID_DEVICE_INFO_MOUSE mouse;
        [FieldOffset(0)] public RID_DEVICE_INFO_HID hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RID_DEVICE_INFO
    {
        public uint cbSize;
        public uint dwType;
        public RID_DEVICE_INFO_UNION info;
    }

    [DllImport("user32.dll")]
    internal static extern uint GetRawInputDeviceList(
        [Out] RAWINPUTDEVICELIST[]? pDeviceList,
        ref uint pNumDevices,
        uint cbSize);

    [DllImport("user32.dll")]
    internal static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll")]
    internal static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        [Out] char[]? pData,
        ref uint pcbSize);

    [DllImport("user32.dll")]
    internal static extern bool RegisterRawInputDevices(
        RAWINPUTDEVICE[] pDevices,
        uint uiDevices,
        uint cbSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }
}
