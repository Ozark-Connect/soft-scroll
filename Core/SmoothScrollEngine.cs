using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using SoftScroll.Native;
using SoftScroll.Settings;

namespace SoftScroll.Core;

public sealed class SmoothScrollEngine : IDisposable
{
    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _running;
    private readonly ManualResetEventSlim _signal = new(false);

    private Axis _v = new();
    private Axis _h = new();

    private AppSettings _s = AppSettings.CreateDefault();

    // Use constants from ScrollConstants
    private static readonly int WHEEL_DELTA = ScrollConstants.WHEEL_DELTA;
    private static readonly int EMIT_UNIT = ScrollConstants.EMIT_UNIT;
    private static readonly double BASE_STEP_PX = ScrollConstants.BASE_STEP_PX;
    private static readonly int PULSE_CLAMP_MIN = ScrollConstants.PULSE_CLAMP_MIN;
    private static readonly int PULSE_CLAMP_MAX = ScrollConstants.PULSE_CLAMP_MAX;

    // Display refresh rate — detected lazily on first Start() to avoid blocking startup
    private static int? DisplayRefreshRate;
    private static readonly object _refreshLock = new();

    private enum ScrollAxis
    {
        None,
        Vertical,
        Horizontal
    }

    private ScrollAxis _lastAxis = ScrollAxis.None;

    // Adaptive frame rate: match display Hz for smoothness, drop to 60fps when idle
    private double _targetFrameMs = 1000.0 / 120; // default 120fps for new instances
    private long _lastWorkTime;

    // Stacked glide speed limit: the per-frame pulse cap at the active frame rate. Faster pulses are dropped anyway.
    private double MaxGlidePxPerMs => PULSE_CLAMP_MAX * EMIT_UNIT / (double)WHEEL_DELTA * BASE_STEP_PX / _targetFrameMs;

    private const double SPIN_WAIT_COUNT = 10;
    private const int IDLE_TIMEOUT_MS = 2000; // drop to 60fps after 2s idle

    public SmoothScrollEngine(AppSettings settings)
    {
        ApplySettings(settings);
    }

    public void ApplySettings(AppSettings s)
    {
        lock (_lock)
        {
            _s = s;
        }
    }

    public void Start()
    {
        // Detect display refresh rate on first start (lazy to avoid blocking app startup)
        if (!DisplayRefreshRate.HasValue)
        {
            lock (_refreshLock)
            {
                if (!DisplayRefreshRate.HasValue)
                    DisplayRefreshRate = NativeMethods.GetDisplayRefreshRate();
            }
            // Target frame rate: match display refresh if >= 60Hz, floor at 120fps
            _targetFrameMs = DisplayRefreshRate.Value >= 120 ? 1000.0 / DisplayRefreshRate.Value : 1000.0 / 120;
        }

        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Worker) { IsBackground = true, Name = "SmoothScrollEngine" };
            _thread.Start();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _running = false;
            // Reset axis state inside lock to avoid race with worker thread
            _v = new();
            _h = new();
        }
        _signal.Set();
        _thread?.Join(1000);
    }

    public void OnWheel(int delta) => OnVerticalWheel(delta, null);

    /// <summary>
    /// Vertical wheel for an app with its own profile. The profile settings stay
    /// attached to the axis so the whole animation (duration, easing, momentum) uses them.
    /// </summary>
    public void OnWheelWithSettings(int delta, AppSettings customSettings) => OnVerticalWheel(delta, customSettings);

    private void OnVerticalWheel(int delta, AppSettings? profile)
    {
        lock (_lock)
        {
            if (_lastAxis == ScrollAxis.Horizontal)
            {
                _h = new();
            }

            var s = profile ?? _s;
            var dir = s.ReverseWheelDirection ? -1 : 1;
            var now = Environment.TickCount64;
            var root = RootUnderCursor();
            if (_v.TargetRoot != root) _v = new() { TargetRoot = root };
            _v.Profile = profile;
            _v.Modifiers = HeldModifiers();
            _v.RegisterNotch(now, delta * dir, s, MaxGlidePxPerMs);
            _lastAxis = ScrollAxis.Vertical;
        }
        _signal.Set();
    }

    /// <param name="delta">Horizontal delta in WM_MOUSEHWHEEL convention (positive = right).</param>
    /// <param name="fromShiftWheel">True when the input was Shift + vertical wheel. Output is then
    /// re-injected as a vertical wheel while Shift is held, which every Shift+wheel app understands.</param>
    public void OnHWheel(int delta, bool fromShiftWheel = false)
    {
        lock (_lock)
        {
            if (_lastAxis == ScrollAxis.Vertical)
            {
                _v = new();
            }

            // No ReverseWheelDirection for horizontal: scrolling right (positive delta)
            // must always mean "scroll right" per Windows convention.
            var now = Environment.TickCount64;
            var root = RootUnderCursor();
            if (_h.TargetRoot != root) _h = new() { TargetRoot = root };
            _h.Profile = null;
            _h.FromShiftWheel = fromShiftWheel;
            _h.Modifiers = HeldModifiers();
            _h.RegisterNotch(now, delta, _s, MaxGlidePxPerMs);
            _lastAxis = ScrollAxis.Horizontal;
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
                // Check if there's anything to emit
                bool workAvailable;
                double remainingTotal;
                bool shiftWheel;
                lock (_lock)
                {
                    workAvailable = _v.HasWork(_s) || _h.HasWork(_s);
                    remainingTotal = Math.Abs(_v.RemainingPx) + Math.Abs(_h.RemainingPx);
                    shiftWheel = _h.FromShiftWheel;
                }

                if (!workAvailable)
                {
                    WheelDiagnostics.GestureEnded();
                    // Block until a wheel event signals us or timeout elapses.
                    // Timeout guarantees eventual shutdown even if no signal arrives.
                    _signal.Wait(TimeSpan.FromMilliseconds(100));
                    _signal.Reset();
                    // Reset time base after idle to prevent frame-1 jitter on new notch
                    lastMs = sw.Elapsed.TotalMilliseconds;
                    _lastWorkTime = Environment.TickCount64;
                    continue;
                }

                var nowMs = sw.Elapsed.TotalMilliseconds;
                var dt = Math.Max(1.0, nowMs - lastMs);
                lastMs = nowMs;
                _lastWorkTime = Environment.TickCount64;

                // Adaptive frame rate computation
                var frameMs = ComputeAdaptiveFrameMs(remainingTotal);
                var root = RootUnderCursor();
                var modifiers = HeldModifiers();

                int outV = 0, outH = 0;
                lock (_lock)
                {
                    // Cursor moved to another window, or another app came to the front under it
                    if (_v.TargetRoot != root) _v = new();
                    if (_h.TargetRoot != root) _h = new();

                    // A modifier pressed after the last notch stops the scroll. Wheel pulses sent while
                    // it is down become Alt+wheel (browser back/forward), Ctrl+wheel (zoom) or Shift+wheel.
                    if ((modifiers & ~_v.Modifiers) != 0) _v = new();
                    if ((modifiers & ~_h.Modifiers) != 0) _h = new();

                    outV = _v.Step(dt, _s);
                    if (_s.HorizontalSmoothness) outH = _h.Step(dt, _s);
                    else { outH = 0; _h = new(); } // setting turned off mid-animation: drop it, or the worker never idles
                }

                // Buffered SendInput: emit both axes in a single call
                if (outV != 0 || outH != 0) SendWheel(outV, outH, shiftWheel);

                WaitUntil(sw, nowMs + frameMs);
            }
            catch (Exception ex)
            {
                // Prevent worker thread from dying silently
                System.Diagnostics.Debug.WriteLine($"SmoothScrollEngine worker: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Waits for an absolute frame deadline. Thread.Sleep alone rounds to whole milliseconds
    /// (3 or 5 ms around a 4.2 ms frame), and the uneven event spacing shows as judder.
    /// Sleeps in 1 ms slices while far away, then spins for the last stretch.
    /// </summary>
    internal static void WaitUntil(Stopwatch sw, double deadlineMs)
    {
        while (deadlineMs - sw.Elapsed.TotalMilliseconds > 1.5)
            Thread.Sleep(1);
        while (sw.Elapsed.TotalMilliseconds < deadlineMs)
            Thread.SpinWait(20);
    }

    /// <summary>
    /// Adaptive frame rate: scales from target (display Hz / 120) down to 60fps when idle.
    /// When remaining scroll is small (&lt; 50px) and no recent notch, drop to 60fps to save CPU.
    /// When remaining is large or recent rapid notches, ramp up to target Hz.
    /// </summary>
    private double ComputeAdaptiveFrameMs(double remainingPx)
    {
        var idleTime = Environment.TickCount64 - _lastWorkTime;

        // Idle ≥ 2s → drop to 60fps
        if (idleTime >= IDLE_TIMEOUT_MS)
            return 1000.0 / 60;

        // Active scrolling: use target (display-matched) frame rate
        return _targetFrameMs;
    }

    private static void SendWheel(int mouseData, int hMouseData, bool hFromShiftWheel)
    {
        // All output goes through SendInput so Windows routes it like real input:
        // to the control under the cursor, not only to the top-level window.
        // Horizontal values use the WM_MOUSEHWHEEL convention (positive = right).
        // Shift+wheel input is re-emitted as a vertical wheel while Shift is still held,
        // so the sign flips back (Shift + wheel up = left). Tilt-wheel input is re-emitted
        // as MOUSEEVENTF_HWHEEL unchanged. See GitHub issue #13.
        var inputs = new NativeMethods.INPUT[2];
        uint count = 0;

        if (hMouseData != 0)
        {
            // If Shift was released mid-animation, a vertical wheel would scroll vertically; use HWHEEL
            var shiftHeld = (NativeMethods.GetAsyncKeyState(NativeMethods.VK_SHIFT) & 0x8000) != 0;
            inputs[count++] = hFromShiftWheel && shiftHeld
                ? NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_WHEEL, -hMouseData)
                : NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_HWHEEL, hMouseData);
        }

        if (mouseData != 0)
        {
            inputs[count++] = NativeMethods.MouseInput(NativeMethods.MOUSEEVENTF_WHEEL, mouseData);
        }

        if (count > 0)
            NativeMethods.SendInput(count, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static IntPtr RootUnderCursor()
    {
        if (!NativeMethods.GetCursorPos(out var pt)) return IntPtr.Zero;
        var hwnd = NativeMethods.WindowFromPoint(pt);
        return hwnd == IntPtr.Zero ? IntPtr.Zero : NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
    }

    private const int MOD_SHIFT = 1, MOD_CTRL = 2, MOD_ALT = 4, MOD_WIN = 8;

    private static int HeldModifiers()
    {
        static bool Down(ushort vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
        return (Down(NativeMethods.VK_SHIFT) ? MOD_SHIFT : 0)
            | (Down(NativeMethods.VK_CONTROL) ? MOD_CTRL : 0)
            | (Down(NativeMethods.VK_MENU) ? MOD_ALT : 0)
            | (Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN) ? MOD_WIN : 0);
    }

    /// <summary>
    /// Stops the momentum glide on both axes, and stops a flick that has not handed off yet
    /// from starting one. A notch animation in progress still finishes. Called on any click or key press.
    /// </summary>
    public void CancelGlide()
    {
        lock (_lock)
        {
            _v.CancelGlide();
            _h.CancelGlide();
        }
    }

    public void ResetHorizontalAxis()
    {
        lock (_lock)
        {
            _h = new();
        }
    }

    public void Dispose() => Stop();

    public static double ComputeEasingFraction(double dtMs, double duration, EasingMode mode, double tailToHeadRatio, bool easingEnabled)
    {
        if (!easingEnabled || mode == EasingMode.Linear)
        {
            return Math.Min(1.0, dtMs / duration);
        }

        var t = dtMs / duration;

        return mode switch
        {
            EasingMode.CubicOut => 1.0 - Math.Pow(1.0 - Math.Min(t, 1.0), 3),
            EasingMode.QuinticOut => 1.0 - Math.Pow(1.0 - Math.Min(t, 1.0), 5),
            _ => 1.0 - Math.Exp(-(2.0 + tailToHeadRatio) * t) // ExponentialOut (default)
        };
    }

    private struct Axis
    {
        public double RemainingPx;
        public long LastNotchTime;
        public int AccelFactor;
        public double UnitAccum;

        // Settings of the app profile that started this animation; null = global settings
        public AppSettings? Profile;
        // Top-level window under the cursor at the last notch. Output follows the cursor,
        // so the scroll stops when this changes rather than carrying into another app.
        public IntPtr TargetRoot;
        // Horizontal axis only: input came from Shift + vertical wheel
        public bool FromShiftWheel;
        // Modifier keys held at the last notch (MOD_* flags)
        public int Modifiers;

        // Acceleration counts full notches (120 units), not events, so hi-res wheels
        // and touchpads that send many small deltas do not hit max acceleration instantly
        private double _accelNotches;

        // Momentum fields
        public double Velocity;       // px/ms, only while InMomentum
        public bool InMomentum;
        // Notches in the current flick (each within FLING_NOTCH_MS of the previous one)
        private int _flingNotches;

        // A gesture glides only if it was a flick: at least two quick notches
        private const int FLING_NOTCH_MS = 150;
        // Glide takes over this long after the last notch
        private const int MOMENTUM_HANDOFF_MS = 30;
        private const double MOMENTUM_STOP_PX_PER_MS = 0.02;
        // Carried-over acceleration fades with this time constant during a pause between flicks
        private const double ACCEL_DECAY_MS = 400;
        private const double GLIDE_STACK_GAIN = 0.5;

        public bool HasWork(AppSettings global)
        {
            return Math.Abs(RemainingPx) >= 0.1
                || UnitAccum != 0
                || InMomentum;
        }

        public void CancelGlide()
        {
            if (InMomentum)
            {
                // Drop the sub-pulse remainder too, or it emits one last pulse after the stop
                InMomentum = false;
                Velocity = 0;
                UnitAccum = 0;
            }
            _flingNotches = 0;
        }

        public void RegisterNotch(long nowMs, int delta, AppSettings s, double maxGlidePxPerMs)
        {
            var gap = nowMs - LastNotchTime;
            var moving = InMomentum || Math.Abs(RemainingPx) >= 0.1;
            var movingSign = InMomentum ? Math.Sign(Velocity) : Math.Sign(RemainingPx);
            var continuing = moving && movingSign == Math.Sign(delta);

            if (moving && !continuing)
            {
                // Reverse input stops the scroll dead, as on macOS, and starts a fresh gesture
                var f = FrictionPerMs(s);
                WheelDiagnostics.ReverseStop(Math.Abs(RemainingPx) + (InMomentum ? Math.Abs(Velocity) / -Math.Log(1.0 - f) : 0), gap, delta);
                InMomentum = false;
                Velocity = 0;
                RemainingPx = 0;
                UnitAccum = 0;
                _accelNotches = 0;
                _flingNotches = 0;
            }

            // A notch while the scroll is still moving the same way continues the gesture:
            // the flick count and acceleration carry over the pause between flicks
            _flingNotches = gap <= FLING_NOTCH_MS || continuing ? _flingNotches + 1 : 1;

            if (gap <= s.AccelerationDeltaMs)
                _accelNotches += Math.Abs(delta) / (double)WHEEL_DELTA;
            else if (continuing)
                _accelNotches *= Math.Exp(-(gap - s.AccelerationDeltaMs) / ACCEL_DECAY_MS);
            else
                _accelNotches = 0;
            AccelFactor = Math.Clamp(1 + (int)_accelNotches, 1, Math.Max(1, s.AccelerationMax));

            LastNotchTime = nowMs;

            var notches = delta / (double)WHEEL_DELTA;
            var pixels = notches * s.StepSizePx * AccelFactor;
            WheelDiagnostics.Requested(Math.Abs(pixels));
            WheelDiagnostics.Notch(delta, gap, AccelFactor, InMomentum, Velocity, RemainingPx);

            if (InMomentum)
            {
                // Stack onto the glide: add the speed this notch gives the eased animation from rest
                // (its first-millisecond fraction), so repeated flicks compound as on macOS.
                // GLIDE_STACK_GAIN scales the accelerated distance so a flick during a glide adds
                // about the speed the same flick hands off from rest.
                var friction = FrictionPerMs(s);
                var k = -Math.Log(1.0 - friction);
                var rate = ComputeEasingFraction(1.0, Math.Max(1.0, s.AnimationTimeMs), s.EasingMode, s.TailToHeadRatio, s.AnimationEasing);
                var unclamped = Velocity + pixels * GLIDE_STACK_GAIN * rate;
                // The cap limits gain only: a same-direction notch never slows a glide that is
                // already faster (the hand-off can start above the cap)
                var limit = Math.Max(maxGlidePxPerMs, Math.Abs(Velocity));
                Velocity = Math.Clamp(unclamped, -limit, limit);
                WheelDiagnostics.VelocityCapLost(Math.Abs(unclamped - Velocity) / k);
            }
            else
            {
                RemainingPx += pixels;
            }
        }

        public int Step(double dtMs, AppSettings global)
        {
            var s = Profile ?? global;
            var duration = Math.Max(1.0, s.AnimationTimeMs);
            var friction = FrictionPerMs(s);

            if (InMomentum && !s.MomentumEnabled)
            {
                InMomentum = false;
                Velocity = 0;
            }

            // Momentum hand-off: shortly after the last notch of a flick, the glide takes over
            // at the speed the animation is moving right now, so there is no stop and restart.
            // It only takes over if the glide would travel further than the animation has left.
            if (s.MomentumEnabled && !InMomentum && _flingNotches >= 2 && Math.Abs(RemainingPx) >= 0.1
                && Environment.TickCount64 - LastNotchTime > MOMENTUM_HANDOFF_MS)
            {
                var frac = ComputeEasingFraction(dtMs, duration, s.EasingMode, s.TailToHeadRatio, s.AnimationEasing);
                var velocity = RemainingPx * frac / dtMs;
                var glidePx = velocity / -Math.Log(1.0 - friction);
                if (Math.Abs(glidePx) > Math.Abs(RemainingPx))
                {
                    Velocity = velocity;
                    RemainingPx = 0;
                    InMomentum = true;
                }
                _flingNotches = 0; // decide once per gesture
            }

            if (InMomentum)
            {
                UnitAccum += PxToUnits(Velocity * dtMs);
                Velocity *= Math.Pow(1.0 - friction, dtMs);
                if (Math.Abs(Velocity) < MOMENTUM_STOP_PX_PER_MS)
                {
                    InMomentum = false;
                    Velocity = 0;
                }
            }
            else if (Math.Abs(RemainingPx) >= 0.1)
            {
                // Normal smooth scroll
                var frac = ComputeEasingFraction(dtMs, duration, s.EasingMode, s.TailToHeadRatio, s.AnimationEasing);

                var emitPx = RemainingPx * frac;
                RemainingPx -= emitPx;
                UnitAccum += PxToUnits(emitPx);
            }

            // Final frame: once less than half a pulse is left, fold the leftover distance into the
            // accumulator and round it. The gesture emits its full wheel distance, and the last
            // pulse is not held back for the long exponential tail.
            var finishing = !InMomentum && Math.Abs(PxToUnits(RemainingPx)) < 0.5;
            if (finishing)
            {
                UnitAccum += PxToUnits(RemainingPx);
                RemainingPx = 0;
            }

            var pulses = finishing ? (int)Math.Round(UnitAccum) : (int)UnitAccum;
            var emitted = Math.Clamp(pulses, PULSE_CLAMP_MIN, PULSE_CLAMP_MAX);
            // The per-frame clamp is a speed limit: pulses above it are dropped, not queued.
            // Queuing them made scrolling continue at full speed after the wheel stopped.
            UnitAccum = finishing ? 0 : UnitAccum - pulses;
            WheelDiagnostics.ClampDropped(UnitsToPx(Math.Abs(pulses - emitted)));
            WheelDiagnostics.Emitted(UnitsToPx(Math.Abs(emitted)));

            return emitted * EMIT_UNIT;
        }

        private static double PxToUnits(double px) => px / BASE_STEP_PX * WHEEL_DELTA / EMIT_UNIT;
        private static double UnitsToPx(double units) => units * EMIT_UNIT / WHEEL_DELTA * BASE_STEP_PX;

        // Glide friction: higher value = stops faster. Scale 0-100 to 0.001-0.006 per ms.
        // 50 gives 0.0035 per ms (time constant about 285 ms).
        private static double FrictionPerMs(AppSettings s) => 0.001 + (s.MomentumFriction / 100.0) * 0.005;
    }
}
