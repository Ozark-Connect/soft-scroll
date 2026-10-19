using System;
using System.Windows;
using Serilog;
using SoftScroll.Core;
using SoftScroll.Hooks;
using SoftScroll.Native;
using SoftScroll.Settings;
using SoftScroll.Infrastructure;
using SoftScroll.UI;

namespace SoftScroll;

public partial class App : System.Windows.Application
{
    private TrayIcon? _tray;
    private GlobalMouseHook? _hook;
    private GlobalHotkey? _hotkey;
    private SettingsViewModel? _vm;
    private SmoothScrollEngine? _engine;
    private ZoomSmoothEngine? _zoomEngine;
    private MiddleClickScrollEngine? _middleClickEngine;
    private MiddleClickOverlay? _middleClickOverlay;
    private ScrollIndicator? _scrollIndicator;
    private SettingsWindow? _settingsWindow;
    private InputDeviceDetector? _inputDetector;
    private RawInputListener? _rawInputListener;
    // Replaced on the UI thread, read by the hook thread
    private volatile AppSettings _settings = null!;

    // Static event for device state changes - used by SettingsWindow to update UI
    public static event EventHandler<DeviceStateEventArgs>? DeviceStateChanged;

    // Debounced exclusion: check process name every 50 ms instead of every wheel event
    private readonly object _exclusionLock = new();
    private string? _lastExcludedProcess;
    private long _lastExcludedCheck;
    private const long EXCLUSION_CHECK_MS = 50;

    // Disable-while-held: track modifier keys and middle button state
    private readonly KeyboardStateSampler _disableKeysSampler = new();
    private volatile bool _middleButtonHeld;

    // Hook thread only: the current middle press was swallowed for middle-click scroll
    private bool _middleClickSwallowed;

    // Single instance: two copies would both hook the wheel and smooth every scroll twice
    private const string InstanceMutexName = @"Local\SoftScroll.SingleInstance";
    private const string ActivateEventName = @"Local\SoftScroll.ShowSettings";
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.EventWaitHandle? _activateEvent;
    private bool _isPrimaryInstance;

    /// <summary>True once the user chose Exit; the settings window then really closes.</summary>
    public static bool IsShuttingDown { get; private set; }
    private bool _trayHintShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new System.Threading.Mutex(true, InstanceMutexName, out _isPrimaryInstance);
        if (!_isPrimaryInstance)
        {
            // Ask the running instance to show its settings window, then exit
            try
            {
                using var activate = System.Threading.EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch (System.Threading.WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }

        _activateEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ActivateEventName);
        new System.Threading.Thread(() =>
        {
            while (_activateEvent.WaitOne())
                Dispatcher.InvokeAsync(() => ShowSettingsWindow());
        }) { IsBackground = true, Name = "SingleInstanceListener" }.Start();

        // Logoff/shutdown must not be cancelled by the settings window hiding to the tray
        SessionEnding += (_, _) => IsShuttingDown = true;

        LoggingConfig.Configure();

        NativeMethods.timeBeginPeriod(1);

        base.OnStartup(e);

        bool firstRun = !System.IO.File.Exists(AppSettings.GetConfigPath());
        _settings = AppSettings.Load();
        if (firstRun) _settings.Save(); // later launches are no longer first runs
        _vm = new SettingsViewModel(_settings);
        _vm.SettingsChanged += (_, _) =>
        {
            _tray?.UpdateEnabled(_vm.Enabled);
            _tray?.RefreshLocalization();
            var snapshot = _vm.Snapshot();
            _settings = snapshot;
            _engine?.ApplySettings(snapshot);
            _middleClickEngine?.UpdateDeadZone(snapshot.MiddleClickDeadZone);
            _middleClickEngine?.UpdateInvertDirection(snapshot.MiddleClickConfig.InvertScrollDirection);
            _scrollIndicator?.ApplySettings(snapshot.IndicatorPosition, snapshot.ScrollIndicatorDurationMs);
            if (_hook != null) _hook.ShiftKeyHorizontal = snapshot.ShiftKeyHorizontal;
            UpdateDisableWhileHolding();
            if (snapshot.AutoDisableOnTouchpad) _rawInputListener?.Initialize();
        };

        _tray = new TrayIcon(_settings.Enabled);
        _tray.OpenSettingsRequested += (_, _) => ShowSettingsWindow();
        _tray.ExitRequested += (_, _) => { IsShuttingDown = true; Shutdown(); };
        _tray.EnabledToggled += (_, enabled) =>
        {
            if (_vm is null) return;
            _vm.Enabled = enabled; // raises SettingsChanged, which refreshes _settings
            _settings.Save();
            UpdateHookState();
        };
        _tray.ToggleHotkeyRequested += (_, _) => ToggleEnabled();

        _engine = new SmoothScrollEngine(_settings);
        _zoomEngine = new ZoomSmoothEngine();
        _middleClickEngine = new MiddleClickScrollEngine();
        _middleClickEngine.UpdateDeadZone(_settings.MiddleClickDeadZone);
        _middleClickEngine.UpdateInvertDirection(_settings.MiddleClickConfig.InvertScrollDirection);

        _middleClickEngine.Activated += (x, y) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                _middleClickOverlay ??= new MiddleClickOverlay();
                // GetCursorPos on the UI thread returns coordinates in the same DPI space as WPF;
                // hook coordinates are physical and differ on mixed-DPI setups
                var pos = GetCursorPosition();
                _middleClickOverlay.ShowAt(pos.X, pos.Y);
            });
        };
        _middleClickEngine.Deactivated += () =>
        {
            Dispatcher.InvokeAsync(() => _middleClickOverlay?.HideOverlay());
        };
        _middleClickEngine.DirectionChanged += (nx, ny, magnitude) =>
        {
            // Raised on the engine thread; the overlay is a WPF window
            Dispatcher.InvokeAsync(() => _middleClickOverlay?.UpdateDirection(nx, ny, magnitude));
        };

        _hook = new GlobalMouseHook();
        _hook.ShiftKeyHorizontal = _settings.ShiftKeyHorizontal;

        // Initialize touchpad detection
        _inputDetector = new InputDeviceDetector();

        // Subscribe to device type changes to update UI
        _inputDetector.DeviceTypeChanged += (_, isTouchpad) =>
        {
            Dispatcher.InvokeAsync(() => {
                UpdateDeviceStateUI(isTouchpad, _inputDetector.TouchpadCount, _inputDetector.MouseCount);
                _tray?.UpdateTouchpadState(isTouchpad);
            });
        };

        // Initialize Raw Input listener for device tracking
        _rawInputListener = new RawInputListener(_inputDetector);

        // Subscribe to devices changed event for re-enumeration
        _rawInputListener.DevicesChanged += (_, _) =>
        {
            Dispatcher.InvokeAsync(() => UpdateDeviceStateUI(_inputDetector.LastEventFromTouchpad, _inputDetector.TouchpadCount, _inputDetector.MouseCount));
        };

        // Setup hotkey for quick toggle (Ctrl+Alt+S)
        if (_settings.EnableGlobalHotkey)
        {
            _hotkey = new GlobalHotkey(
                id: 1,
                modifiers: HotkeyConstants.MOD_CONTROL | HotkeyConstants.MOD_ALT | HotkeyConstants.MOD_NOREPEAT,
                key: HotkeyConstants.VK_S
            );
            _hotkey.HotkeyPressed += (_, _) => Dispatcher.InvokeAsync(ToggleEnabled);
            _hotkey.Install();
        }

        _hook.MouseWheel += (_, args) =>
        {
            if (!_settings.Enabled) return;
            if (IsDisabledByHold()) return;
            if (IsExcludedApp()) return;
            if (IsOwnWindow()) return;

            // Auto-disable on touchpad if setting is enabled
            if (_settings.AutoDisableOnTouchpad)
            {
                if (_inputDetector?.ShouldDisableSmoothScroll() == true)
                    return;
            }

            // Show scroll indicator if enabled
            if (_settings.ShowScrollIndicator)
            {
                ShowScrollIndicator(args.Delta);
            }

            // Check for app-specific profile
            string? procName;
            lock (_exclusionLock) { procName = _lastExcludedProcess; }
            var profile = _settings.GetAppProfile(procName ?? "");
            ResetHorizontalCarryIfNeeded();
            args.Handled = true;
            if (profile != null && profile.Enabled)
                _engine!.OnWheelWithSettings(args.Delta, profile.ToAppSettings(_settings));
            else
                _engine!.OnWheel(args.Delta);
            ScrollStatistics.Instance.RecordScroll(args.Delta);
        };
        _hook.MouseHWheel += (_, args) =>
        {
            if (!_settings.Enabled) return;
            if (IsDisabledByHold()) return;
            if (IsExcludedApp()) return;
            if (IsOwnWindow()) return;

            // Auto-disable on touchpad if setting is enabled
            if (_settings.AutoDisableOnTouchpad)
            {
                if (_inputDetector?.ShouldDisableSmoothScroll() == true)
                    return;
            }

            // Only intercept when horizontal smoothness is enabled.
            // When disabled, leave args.Handled = false so the native WM_MOUSEHWHEEL
            // is delivered to the target window unchanged. See GitHub issue #13.
            if (!_settings.HorizontalSmoothness)
                return;

            args.Handled = true;
            _lastScrollWasHorizontal = true;
            _engine!.OnHWheel(args.Delta, _hook.LastHWheelFromShift);
            ScrollStatistics.Instance.RecordScroll(args.Delta);
        };
        _hook.MouseZoomWheel += (_, args) =>
        {
            if (!_settings.Enabled || !_settings.ZoomSmoothing) return;
            if (IsDisabledByHold()) return;
            if (IsExcludedApp()) return;
            if (IsOwnWindow()) return;

            args.Handled = true;
            _lastScrollWasHorizontal = false;
            _zoomEngine!.OnZoom(args.Delta);
        };
        _hook.MiddleButtonDown += (_, args) =>
        {
            _middleButtonHeld = true;
            _middleClickSwallowed = false;
            if (!_settings.Enabled || !_settings.MiddleClickScroll) return;
            if (IsExcludedApp()) return;
            if (IsOwnWindow()) return;

            // Swallow the press so the target app does not also start its own
            // autoscroll or open a link; a press without scrolling is replayed on release
            args.Handled = true;
            _middleClickSwallowed = true;
            _middleClickEngine!.OnMiddleDown(args.X, args.Y);
        };
        _hook.MiddleButtonUp += (_, _) =>
        {
            _middleButtonHeld = false;
            if (!_middleClickSwallowed) return;
            _middleClickSwallowed = false;

            var scrolled = _middleClickEngine!.OnMiddleUp();
            if (!scrolled)
                System.Threading.ThreadPool.QueueUserWorkItem(_ => MiddleClickScrollEngine.ReplayMiddleClick());
        };
        _hook.MouseMoved += (_, args) =>
        {
            _middleClickEngine?.OnMouseMove(args.X, args.Y);
        };

        UpdateHookState();

        // Start in the tray whenever "Start minimized" is on, not only when launched at sign-in.
        // First run (no settings file yet) still shows the window so the user finds the app.
        bool shouldStartMinimized = _settings.StartMinimized && !firstRun;

        if (!shouldStartMinimized)
        {
            ShowSettingsWindow(show: true, startMinimized: false);
        }
        else
        {
            // Show tray icon but don't show the settings window
            ShowSettingsWindow(show: false, startMinimized: true);
            Log.Information("Starting minimized to system tray");
        }

        // Enumerate devices first so the first raw input can be classified, then register.
        // The listener owns its own hidden window, so it works when starting minimized too.
        _inputDetector.EnumerateDevices();
        if (_settings.AutoDisableOnTouchpad)
            _rawInputListener.Initialize();

        Current.MainWindow = _settingsWindow;
    }

    private void ToggleEnabled()
    {
        var newState = !_settings.Enabled;
        _settings.Enabled = newState;
        _settings.Save();
        _vm!.Enabled = newState;
        _tray?.UpdateEnabled(newState);
        UpdateHookState();
        Log.Information("Soft Scroll {State}", newState ? "enabled" : "disabled");
    }

    private bool _lastScrollWasHorizontal;

    private void ResetHorizontalCarryIfNeeded()
    {
        if (!_lastScrollWasHorizontal)
            return;

        _engine?.ResetHorizontalAxis();
        _lastScrollWasHorizontal = false;
    }
    private const long OWN_WINDOW_CHECK_MS = 50;
    private readonly int _ownProcessId = Environment.ProcessId;
    private bool _isOwnWindow;
    private IntPtr _lastWindowUnderCursor;
    private long _lastOwnWindowCheck;

    /// <summary>
    /// True when the window under the cursor belongs to Soft Scroll. Wheel input goes to the
    /// window under the cursor, not the foreground window, so testing focus would turn off
    /// smoothing for every app while the settings window is focused.
    /// </summary>
    private bool IsOwnWindow()
    {
        var now = Environment.TickCount64;
        if (now - _lastOwnWindowCheck <= OWN_WINDOW_CHECK_MS)
            return _isOwnWindow;
        _lastOwnWindowCheck = now;

        if (!NativeMethods.GetCursorPos(out var pt))
            return _isOwnWindow;

        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd != IntPtr.Zero)
            hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (hwnd == _lastWindowUnderCursor)
            return _isOwnWindow;

        _lastWindowUnderCursor = hwnd;
        if (hwnd == IntPtr.Zero)
        {
            _isOwnWindow = false;
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        _isOwnWindow = pid == (uint)_ownProcessId;
        return _isOwnWindow;
    }

    private bool IsExcludedApp()
    {
        var now = Environment.TickCount64;
        if (now - _lastExcludedCheck > EXCLUSION_CHECK_MS)
        {
            _lastExcludedProcess = CachedProcessHelper.GetProcessUnderCursor();
            _lastExcludedCheck = now;
        }
        if (_settings.IsWhitelistMode)
            return !_settings.IsWhitelisted(_lastExcludedProcess);
        return _settings.IsExcluded(_lastExcludedProcess);
    }

    /// <summary>
    /// Runs the modifier sampler only while a disable-while-holding key is configured.
    /// Called on every settings change, so keys checked at runtime take effect at once.
    /// The middle button state is tracked by the hook handlers unconditionally.
    /// </summary>
    private void UpdateDisableWhileHolding()
    {
        if (_settings.Enabled && _settings.DisableWhileHoldingKeys.Count > 0)
            _disableKeysSampler.Start();
        else
            _disableKeysSampler.Stop();
    }

    private bool IsDisabledByHold()
    {
        foreach (var key in _settings.DisableWhileHoldingKeys)
        {
            var vk = key.ToUpperInvariant();
            if (vk == "CTRL" && _disableKeysSampler.IsCtrlPressed) return true;
            if (vk == "SHIFT" && _disableKeysSampler.IsShiftPressed) return true;
            if (vk == "ALT" && _disableKeysSampler.IsAltPressed) return true;
            if (vk == "WIN" || vk == "WINDOWS")
            {
                // Win key: check VK_LWIN or VK_RWIN via GetAsyncKeyState
                if ((NativeMethods.GetAsyncKeyState(0x5B) & 0x8000) != 0 ||
                    (NativeMethods.GetAsyncKeyState(0x5C) & 0x8000) != 0)
                    return true;
            }
        }
        if (_settings.DisableWhileHoldingMiddleButton && _middleButtonHeld) return true;
        return false;
    }

    private void ShowScrollIndicator(int speed)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_scrollIndicator == null)
            {
                _scrollIndicator = new ScrollIndicator();
                _scrollIndicator.ApplySettings(_settings.IndicatorPosition, _settings.ScrollIndicatorDurationMs);
            }
            var pos = GetCursorPosition();
            _scrollIndicator.UpdateSpeed(speed);
            if (!_scrollIndicator.IsVisible)
            {
                _scrollIndicator.ShowAt(pos.X, pos.Y, speed);
            }
        });
    }

    private System.Drawing.Point GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out var point);
        return new System.Drawing.Point(point.x, point.y);
    }

    private void UpdateHookState()
    {
        if (_hook is null || _vm is null || _engine is null) return;
        if (_vm.Enabled)
        {
            var snapshot = _vm.Snapshot();
            _engine.ApplySettings(snapshot);
            _hook.ShiftKeyHorizontal = snapshot.ShiftKeyHorizontal;
            _engine.Start();
            _zoomEngine?.Start();
            _middleClickEngine?.Start();
            _hook.Install();
        }
        else
        {
            _hook.Uninstall();
            _engine.Stop();
            _zoomEngine?.Stop();
            _middleClickEngine?.Stop();
            _middleClickEngine?.OnMiddleUp();
            // The hook thread has exited; a press in progress must not replay a click after re-enable
            _middleClickSwallowed = false;
        }
        UpdateDisableWhileHolding();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        base.OnExit(e);
        if (!_isPrimaryInstance) return;
        _hook?.Dispose();
        _hotkey?.Dispose();
        _engine?.Dispose();
        _zoomEngine?.Dispose();
        _middleClickEngine?.Dispose();
        _tray?.Dispose();
        _inputDetector?.Dispose();
        _rawInputListener?.Dispose();
        LoggingConfig.Shutdown();
        NativeMethods.timeEndPeriod(1);
    }

    private void ShowSettingsWindow(bool show = true, bool startMinimized = false)
    {
        if (_vm is null) return;

        // Don't show the window at all when minimized
        if (!show) return;

        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_vm);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.HiddenToTray += (_, _) =>
            {
                if (_trayHintShown) return;
                _trayHintShown = true;
                _tray?.ShowStillRunningHint();
            };
            _settingsWindow.Owner = null;

            // Apply minimized state before showing if needed
            if (startMinimized)
            {
                _settingsWindow.WindowState = WindowState.Minimized;
                _settingsWindow.Show();
                // Ensure it doesn't flash on taskbar
                _settingsWindow.ShowInTaskbar = false;
            }
            else
            {
                _settingsWindow.Show();
            }
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Show();
            _settingsWindow.ShowInTaskbar = true;
            _settingsWindow.Activate();
        }
    }

    /// <summary>
    /// Called to update the UI when device state changes
    /// </summary>
    internal void UpdateDeviceStateUI(bool isTouchpad, int touchpadCount, int mouseCount)
    {
        DeviceStateChanged?.Invoke(this, new DeviceStateEventArgs(isTouchpad, touchpadCount, mouseCount));
    }
}

public class DeviceStateEventArgs : EventArgs
{
    public bool IsTouchpadActive { get; }
    public int TouchpadCount { get; }
    public int MouseCount { get; }

    public DeviceStateEventArgs(bool isTouchpadActive, int touchpadCount, int mouseCount)
    {
        IsTouchpadActive = isTouchpadActive;
        TouchpadCount = touchpadCount;
        MouseCount = mouseCount;
    }
}

