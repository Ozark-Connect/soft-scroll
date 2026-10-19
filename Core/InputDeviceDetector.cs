using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Serilog;
using SoftScroll.Native;

namespace SoftScroll.Core;

/// <summary>
/// Detects and tracks input devices (touchpad vs mouse) to enable
/// automatic smooth scroll disable when using the laptop touchpad.
/// Touchpads are identified by their Precision Touchpad HID collection
/// (usage page Digitizer 0x0D, usage Touch Pad 0x05). The touchpad's own mouse
/// collection (same physical device) is linked to it by device instance path.
/// </summary>
public sealed class InputDeviceDetector : IDisposable
{
    private readonly object _lock = new();

    // Replaced as a whole on enumeration, read without a lock on every WM_INPUT
    private volatile HashSet<IntPtr> _touchpadHandles = new();
    private int _touchpadCount;
    private int _mouseCount;

    // Written on the UI thread (WM_INPUT), read on the mouse hook thread
    private long _lastTouchpadTick;
    private long _lastMouseTick;
    private volatile bool _lastEventFromTouchpad;

    // Precision touchpads report continuously while fingers are on the pad, so a wheel event
    // within this window of the last touchpad report, with no mouse input since, is a touchpad scroll
    private const int TOUCHPAD_ACTIVE_MS = 250;

    public event EventHandler<bool>? DeviceTypeChanged;

    /// <summary>
    /// True if the last input event came from a touchpad.
    /// </summary>
    public bool LastEventFromTouchpad => _lastEventFromTouchpad;

    public int TouchpadCount => Volatile.Read(ref _touchpadCount);

    public int MouseCount => Volatile.Read(ref _mouseCount);

    /// <summary>
    /// Enumerates all connected input devices and records which device handles belong to a touchpad.
    /// </summary>
    public void EnumerateDevices()
    {
        lock (_lock)
        {
            try
            {
                uint deviceCount = 0;
                uint cbSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();
                uint result = NativeMethods.GetRawInputDeviceList(null, ref deviceCount, cbSize);

                if (result != 0 || deviceCount == 0)
                {
                    Log.Warning("[InputDetector] Failed to get device count: {Result}", result);
                    return;
                }

                var devices = new NativeMethods.RAWINPUTDEVICELIST[deviceCount];
                result = NativeMethods.GetRawInputDeviceList(devices, ref deviceCount, cbSize);

                if (result == unchecked((uint)-1))
                {
                    Log.Warning("[InputDetector] Failed to enumerate devices: {Result}", result);
                    return;
                }
                // A device can disappear between the two calls; unused entries are zeroed (type mouse)
                if (result < devices.Length)
                    Array.Resize(ref devices, (int)result);

                var touchpadHandles = new HashSet<IntPtr>();
                var touchpadDeviceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int touchpads = 0;

                foreach (var device in devices)
                {
                    if (device.dwType != NativeMethods.RIM_TYPEHID) continue;
                    var info = GetDeviceInfo(device.hDevice);
                    if (info is { } hid
                        && hid.usUsagePage == NativeMethods.HID_USAGE_PAGE_DIGITIZER
                        && hid.usUsage == NativeMethods.HID_USAGE_DIGITIZER_TOUCH_PAD)
                    {
                        touchpads++;
                        touchpadHandles.Add(device.hDevice);
                        var id = GetPhysicalDeviceId(device.hDevice);
                        if (id != null) touchpadDeviceIds.Add(id);
                        Log.Information("[InputDetector] Found touchpad: {Handle} {Id}", device.hDevice, id);
                    }
                }

                int mice = 0;
                foreach (var device in devices)
                {
                    if (device.dwType != NativeMethods.RIM_TYPEMOUSE) continue;
                    var id = GetPhysicalDeviceId(device.hDevice);
                    if (id != null && touchpadDeviceIds.Contains(id))
                    {
                        // Mouse collection of a touchpad: pointer moves and legacy scroll come from here
                        touchpadHandles.Add(device.hDevice);
                    }
                    else
                    {
                        mice++;
                        Log.Debug("[InputDetector] Found mouse device: {Handle} {Id}", device.hDevice, id);
                    }
                }

                _touchpadHandles = touchpadHandles;
                Volatile.Write(ref _touchpadCount, touchpads);
                Volatile.Write(ref _mouseCount, mice);

                Log.Information("[InputDetector] Enumerated {Count} devices, found {TouchpadCount} touchpad(s), {MouseCount} mouse(s)",
                    deviceCount, touchpads, mice);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[InputDetector] Error during device enumeration");
            }
        }
    }

    private static NativeMethods.RID_DEVICE_INFO_HID? GetDeviceInfo(IntPtr hDevice)
    {
        uint size = (uint)Marshal.SizeOf<NativeMethods.RID_DEVICE_INFO>();
        IntPtr ptr = Marshal.AllocHGlobal((int)size);
        try
        {
            var info = new NativeMethods.RID_DEVICE_INFO { cbSize = size };
            Marshal.StructureToPtr(info, ptr, false);
            uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICEINFO, ptr, ref size);
            if (result == unchecked((uint)-1) || result == 0) return null;

            info = Marshal.PtrToStructure<NativeMethods.RID_DEVICE_INFO>(ptr);
            return info.dwType == NativeMethods.RIM_TYPEHID ? info.info.hid : null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[InputDetector] Error reading device info for {Handle}", hDevice);
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Returns an id shared by all HID collections of one physical device, e.g.
    /// <c>\\?\HID#VID_06CB&amp;PID_CE26&amp;Col02#5&amp;2ab1c1b&amp;0&amp;0001#{guid}</c> becomes
    /// <c>vid_06cb&amp;pid_ce26#5&amp;2ab1c1b&amp;0</c>. The collection suffix and collection index are dropped.
    /// </summary>
    private static string? GetPhysicalDeviceId(IntPtr hDevice)
    {
        try
        {
            uint size = 0;
            NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, (char[]?)null, ref size);
            if (size == 0) return null;

            var buffer = new char[size];
            if (NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, buffer, ref size) == unchecked((uint)-1))
                return null;
            var name = new string(buffer).TrimEnd('\0').ToLowerInvariant();

            var parts = name.Split('#');
            if (parts.Length < 3 || !parts[0].EndsWith("hid")) return null;

            var hardware = parts[1];
            var col = hardware.IndexOf("&col", StringComparison.Ordinal);
            if (col >= 0) hardware = hardware[..col];

            var instance = parts[2];
            var last = instance.LastIndexOf('&');
            if (last > 0) instance = instance[..last];

            return hardware + "#" + instance;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[InputDetector] Error reading device name for {Handle}", hDevice);
            return null;
        }
    }

    /// <summary>
    /// Called from the Raw Input listener for every WM_INPUT.
    /// </summary>
    public void ProcessRawInput(IntPtr hDevice)
    {
        // hDevice is zero for injected input (including our own smoothed output)
        if (hDevice == IntPtr.Zero) return;

        var now = Environment.TickCount64;
        bool isTouchpad = _touchpadHandles.Contains(hDevice);
        if (isTouchpad)
            Volatile.Write(ref _lastTouchpadTick, now);
        else
            Volatile.Write(ref _lastMouseTick, now);

        if (isTouchpad != _lastEventFromTouchpad)
        {
            _lastEventFromTouchpad = isTouchpad;
            Log.Information("[InputDetector] Device changed: {Type}", isTouchpad ? "touchpad" : "mouse/external");
            DeviceTypeChanged?.Invoke(this, isTouchpad);
        }
    }

    /// <summary>
    /// Determines if smooth scroll should be disabled for the wheel event being handled now.
    /// Safe to call from the mouse hook thread.
    /// </summary>
    public bool ShouldDisableSmoothScroll()
    {
        var touchpad = Volatile.Read(ref _lastTouchpadTick);
        var mouse = Volatile.Read(ref _lastMouseTick);
        return touchpad > mouse && Environment.TickCount64 - touchpad < TOUCHPAD_ACTIVE_MS;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
