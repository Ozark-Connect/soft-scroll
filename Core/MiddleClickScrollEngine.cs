using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoftScroll.Native;

namespace SoftScroll.Core;

public sealed class MiddleClickScrollEngine : IDisposable
{
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _signal = new(false);
    private volatile bool _active;

    private int _originX, _originY;
    private int _currentX, _currentY;
    private int _deadZone = 10;
    private volatile bool _invert;
    // True once the cursor left the dead zone during the current press
    private volatile bool _leftDeadZone;

    private static readonly double FRAME_MS = ScrollConstants.FRAME_MS;
    private static readonly int WHEEL_DELTA = ScrollConstants.WHEEL_DELTA;

    public event Action<int, int>? Activated;   // (originX, originY)
    public event Action? Deactivated;
    public event Action<double, double, double>? DirectionChanged; // (normalizedX, normalizedY, magnitude) for overlay

    public void UpdateDeadZone(int px) { lock (_lock) _deadZone = px; }

    public void UpdateInvertDirection(bool invert) => _invert = invert;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "MiddleClickScroll" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock) { _running = false; _active = false; }
        _signal.Set();
        _thread?.Join(1000);
    }

    public void OnMiddleDown(int x, int y)
    {
        lock (_lock)
        {
            _originX = x;
            _originY = y;
            _currentX = x;
            _currentY = y;
            _active = true;
            _leftDeadZone = false;
        }
        Activated?.Invoke(x, y);
        _signal.Set();
    }

    /// <returns>True if the cursor left the dead zone, i.e. the press was used to scroll.</returns>
    public bool OnMiddleUp()
    {
        bool wasActive;
        lock (_lock) { wasActive = _active; _active = false; }
        if (wasActive) Deactivated?.Invoke();
        _signal.Set();
        return _leftDeadZone;
    }

    public void OnMouseMove(int x, int y)
    {
        if (!_active) return;
        lock (_lock)
        {
            _currentX = x;
            _currentY = y;
            if (Math.Abs(x - _originX) > _deadZone || Math.Abs(y - _originY) > _deadZone)
                _leftDeadZone = true;
        }
    }

    private void Worker()
    {
        var sw = Stopwatch.StartNew();
        double lastMs = sw.Elapsed.TotalMilliseconds;
        double accumV = 0, accumH = 0;

        while (_running)
        {
            try
            {
                if (!_active)
                {
                    _signal.Wait(TimeSpan.FromMilliseconds(100));
                    _signal.Reset();
                    // Reset time base after idle to prevent frame-1 jitter
                    lastMs = sw.Elapsed.TotalMilliseconds;
                    accumV = 0;
                    accumH = 0;
                    continue;
                }

                var nowMs = sw.Elapsed.TotalMilliseconds;
                var dt = Math.Max(1.0, nowMs - lastMs);
                lastMs = nowMs;

                int dx, dy, dz;
                lock (_lock)
                {
                    dx = _currentX - _originX;
                    dy = _currentY - _originY;
                    dz = _deadZone;
                }

                // Compute scroll speed from distance (with dead zone)
                double speedV = 0, speedH = 0;
                if (Math.Abs(dy) > dz)
                {
                    var effective = Math.Abs(dy) - dz;
                    speedV = Math.Sign(dy) * SpeedCurve(effective) * dt * 0.01;
                }
                if (Math.Abs(dx) > dz)
                {
                    var effective = Math.Abs(dx) - dz;
                    speedH = Math.Sign(dx) * SpeedCurve(effective) * dt * 0.01;
                }

                // Notify overlay of direction with magnitude
                var maxDist = 200.0;
                var nx = Math.Clamp(dx / maxDist, -1.0, 1.0);
                var ny = Math.Clamp(dy / maxDist, -1.0, 1.0);
                var magnitude = Math.Sqrt(nx * nx + ny * ny);
                DirectionChanged?.Invoke(nx, ny, magnitude);

                accumV += speedV;
                accumH += speedH;

                // Buffered SendInput: emit both axes in a single call
                int vPulses = 0, hPulses = 0;
                if (Math.Abs(accumV) >= 1.0)
                {
                    vPulses = (int)accumV;
                    accumV -= vPulses;
                }
                if (Math.Abs(accumH) >= 1.0)
                {
                    hPulses = (int)accumH;
                    accumH -= hPulses;
                }

                var dir = _invert ? -1 : 1;
                if (vPulses != 0 || hPulses != 0)
                    SendWheel(-vPulses * WHEEL_DELTA * dir, hPulses * WHEEL_DELTA * dir);

                var sleep = FRAME_MS - (sw.Elapsed.TotalMilliseconds - nowMs);
                if (sleep > 0) Thread.Sleep((int)Math.Round(sleep));
                else Thread.SpinWait(ScrollConstants.SPIN_WAIT_COUNT);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"MiddleClickScrollEngine worker: {ex.Message}");
            }
        }
    }

    private static double SpeedCurve(double distance)
    {
        // Quadratic speed curve: slow near dead zone, fast at distance
        return Math.Min(distance * distance * 0.001, 30.0);
    }

    private static void SendWheel(int vMouseData, int hMouseData)
    {
        var size = Marshal.SizeOf<NativeMethods.INPUT>();

        if (hMouseData != 0 && vMouseData != 0)
        {
            var inputs = new[]
            {
                NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_WHEEL, vMouseData),
                NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_HWHEEL, hMouseData),
            };
            NativeMethods.SendInput(2, inputs, size);
        }
        else if (hMouseData != 0)
        {
            NativeMethods.SendInput(1, [NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_HWHEEL, hMouseData)], size);
        }
        else if (vMouseData != 0)
        {
            NativeMethods.SendInput(1, [NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_WHEEL, vMouseData)], size);
        }
    }

    /// <summary>
    /// Re-sends a plain middle click. Used when the button was released without scrolling,
    /// so middle-click actions (open link in new tab, close tab, paste) still work.
    /// </summary>
    public static void ReplayMiddleClick()
    {
        var inputs = new[]
        {
            NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_MIDDLEDOWN),
            NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_MIDDLEUP),
        };
        NativeMethods.SendInput(2, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public void Dispose() => Stop();
}
