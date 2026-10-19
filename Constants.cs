namespace SoftScroll;

public static class ScrollConstants
{
    // Mouse wheel
    public const int WHEEL_DELTA = 120;
    
    // Emission granularity in wheel units. 1 = every frame sends exactly the distance it owes,
    // so the motion has no visible steps. Larger values send coarser, less frequent events.
    public const int EMIT_UNIT = 1;

    // Step calculation
    public const double BASE_STEP_PX = 120.0;

    // Per-frame speed limit, in EMIT_UNITs (240 wheel units = two notches per frame)
    public const int PULSE_CLAMP_MIN = -240;
    public const int PULSE_CLAMP_MAX = 240;
    
    // Frame rate
    public const int FRAME_RATE = 120;
    public const double FRAME_MS = 1000.0 / FRAME_RATE;
    
    // Spin wait for idle CPU
    public const int SPIN_WAIT_COUNT = 10;
}