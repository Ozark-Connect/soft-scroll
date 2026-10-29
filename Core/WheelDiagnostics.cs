using System;
using System.Diagnostics;
using Serilog;

namespace SoftScroll.Core;

/// <summary>
/// TEMPORARY: per-gesture wheel accounting to find where scroll distance is lost.
/// Raw Input = what the mouse driver reported. Hook = what WH_MOUSE_LL saw.
/// Engine = distance requested, emitted, and discarded at each point.
/// One summary line is logged when the vertical scroll comes to rest.
/// </summary>
internal static class WheelDiagnostics
{
    private static readonly object _lock = new();
    private static readonly Stopwatch _sw = Stopwatch.StartNew();

    private static int _rawEvents, _rawUnits, _rawReversals, _rawMaxAbs;
    private static double _rawLastMs, _rawMinGapMs = double.MaxValue;
    private static int _rawLastSign;

    private static int _hookEvents, _hookUnits, _hookReversals;
    private static int _hookLastSign;

    private static double _requestedPx, _emittedPx, _clampDroppedPx, _reverseStopLostPx, _velocityCapLostPx;
    private static int _reverseStops;

    public static void Raw(int delta)
    {
        lock (_lock)
        {
            var now = _sw.Elapsed.TotalMilliseconds;
            if (_rawEvents > 0) _rawMinGapMs = Math.Min(_rawMinGapMs, now - _rawLastMs);
            _rawLastMs = now;
            var sign = Math.Sign(delta);
            if (_rawLastSign != 0 && sign != _rawLastSign)
            {
                _rawReversals++;
                Log.Debug("[WheelDiag] RAW reversal delta={Delta} after {Events} events", delta, _rawEvents);
            }
            _rawLastSign = sign;
            _rawEvents++;
            _rawUnits += delta;
            _rawMaxAbs = Math.Max(_rawMaxAbs, Math.Abs(delta));
        }
    }

    public static void Hook(int delta)
    {
        lock (_lock)
        {
            var sign = Math.Sign(delta);
            if (_hookLastSign != 0 && sign != _hookLastSign) _hookReversals++;
            _hookLastSign = sign;
            _hookEvents++;
            _hookUnits += delta;
        }
    }

    public static void Notch(int delta, long gapMs, int accel, bool inMomentum, double velocity, double remainingPx) =>
        Log.Debug("[WheelDiag] NOTCH d={Delta} gap={Gap}ms accel={Accel}x {Mode} v={V:F1}px/ms remaining={R:F0}px",
            delta, gapMs > 9999 ? -1 : gapMs, accel, inMomentum ? "glide" : "anim", velocity, remainingPx);

    public static void Requested(double px) { lock (_lock) _requestedPx += px; }
    public static void Emitted(double px) { lock (_lock) _emittedPx += px; }
    public static void ClampDropped(double px) { lock (_lock) _clampDroppedPx += px; }
    public static void VelocityCapLost(double px) { lock (_lock) _velocityCapLostPx += px; }

    public static void ReverseStop(double lostPx, long msSinceLastNotch, int delta)
    {
        lock (_lock)
        {
            _reverseStops++;
            _reverseStopLostPx += lostPx;
        }
        Log.Debug("[WheelDiag] REVERSE STOP delta={Delta} {Gap}ms after last notch, discarded {Lost:F0}px",
            delta, msSinceLastNotch, lostPx);
    }

    public static void GestureEnded()
    {
        lock (_lock)
        {
            if (_hookEvents == 0 && _rawEvents == 0) return;
            Log.Debug(
                "[WheelDiag] GESTURE raw: {RawEvents} ev, {RawUnits} units, max |d| {RawMax}, min gap {RawGap:F1}ms, {RawRev} reversals | " +
                "hook: {HookEvents} ev, {HookUnits} units, {HookRev} reversals | " +
                "engine px: requested {Req:F0}, emitted {Emit:F0}, clamp-dropped {Clamp:F0}, reverse-stop lost {Rev:F0} ({RevN}x), velocity-cap lost {Cap:F0}",
                _rawEvents, _rawUnits, _rawMaxAbs, _rawEvents > 1 ? _rawMinGapMs : 0, _rawReversals,
                _hookEvents, _hookUnits, _hookReversals,
                _requestedPx, _emittedPx, _clampDroppedPx, _reverseStopLostPx, _reverseStops, _velocityCapLostPx);

            _rawEvents = _rawUnits = _rawReversals = _rawMaxAbs = 0;
            _rawMinGapMs = double.MaxValue;
            _rawLastSign = 0;
            _hookEvents = _hookUnits = _hookReversals = 0;
            _hookLastSign = 0;
            _requestedPx = _emittedPx = _clampDroppedPx = _reverseStopLostPx = _velocityCapLostPx = 0;
            _reverseStops = 0;
        }
    }
}
