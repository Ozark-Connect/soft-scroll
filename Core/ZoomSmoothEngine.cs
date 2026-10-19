using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoftScroll.Native;

namespace SoftScroll.Core;

public sealed class ZoomSmoothEngine : IDisposable
{
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _signal = new(false);
    private double _remainingDelta;
    private double _unitAccum;

    private const int ZOOM_DURATION_MS = 150;
    private const double FRAME_MS = ScrollConstants.FRAME_MS;
    private const int WHEEL_DELTA = ScrollConstants.WHEEL_DELTA;

    public void Start()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "ZoomSmoothEngine" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            _remainingDelta = 0;
            _unitAccum = 0;
        }
        _signal.Set();
        _thread?.Join(1000);
    }

    public void OnZoom(int delta)
    {
        lock (_lock)
        {
            _remainingDelta += delta;
        }
        _signal.Set();
    }

    private void Worker()
    {
        var sw = Stopwatch.StartNew();
        double lastMs = sw.Elapsed.TotalMilliseconds;

        while (_running)
        {
            try
            {
                bool workAvailable;
                lock (_lock)
                {
                    workAvailable = Math.Abs(_remainingDelta) >= 0.1 || _unitAccum != 0;
                }

                if (!workAvailable)
                {
                    _signal.Wait(TimeSpan.FromMilliseconds(100));
                    _signal.Reset();
                    lastMs = sw.Elapsed.TotalMilliseconds;
                    continue;
                }

                var nowMs = sw.Elapsed.TotalMilliseconds;
                var dt = Math.Max(1.0, nowMs - lastMs);
                lastMs = nowMs;

                int output;
                lock (_lock)
                {
                    output = Step(dt);
                }

                if (output != 0)
                {
                    EmitZoom(output);
                }

                var sleep = FRAME_MS - (sw.Elapsed.TotalMilliseconds - nowMs);
                if (sleep > 0) Thread.Sleep((int)Math.Round(sleep));
                else Thread.SpinWait(ScrollConstants.SPIN_WAIT_COUNT);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ZoomSmoothEngine worker: {ex.Message}");
            }
        }
    }

    private int Step(double dtMs)
    {
        if (Math.Abs(_remainingDelta) >= 0.1)
        {
            var t = Math.Min(1.0, dtMs / ZOOM_DURATION_MS);
            var frac = 1.0 - Math.Pow(1.0 - t, 3); // CubicOut

            var emit = _remainingDelta * frac;
            _remainingDelta -= emit;
            _unitAccum += emit / WHEEL_DELTA;
        }

        // Final frame: once less than half a zoom step is left, fold the leftover into the
        // accumulator and round it, so a single Ctrl+wheel notch produces one zoom step
        // within a few frames instead of waiting for the long tail
        var finishing = Math.Abs(_remainingDelta) < WHEEL_DELTA / 2.0;
        if (finishing)
        {
            _unitAccum += _remainingDelta / WHEEL_DELTA;
            _remainingDelta = 0;
        }

        var pulses = finishing ? (int)Math.Round(_unitAccum) : (int)_unitAccum;
        var emitted = Math.Clamp(pulses, -5, 5);
        // The clamp is a speed limit: steps above it are dropped, not queued for later frames
        _unitAccum = finishing ? 0 : _unitAccum - pulses;

        return emitted * WHEEL_DELTA;
    }

    private static void EmitZoom(int mouseData)
    {
        // SendInput routes the wheel to the control under the cursor like real input.
        // Ctrl is normally still held by the user; inject Ctrl only if it was released
        // mid-animation, so the user's own Ctrl key state is never overridden.
        var ctrlHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_CONTROL) & 0x8000) != 0;
        var inputs = ctrlHeld
            ? new[] { NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_WHEEL, mouseData) }
            : new[]
            {
                NativeMethods.KeyInput(NativeMethods.VK_CONTROL, 0),
                NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_WHEEL, mouseData),
                NativeMethods.KeyInput(NativeMethods.VK_CONTROL, NativeMethods.KEYEVENTF_KEYUP)
            };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    public void Dispose() => Stop();
}
