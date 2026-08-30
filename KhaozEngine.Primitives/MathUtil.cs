namespace KhaozEngine.Primitives;

/// <summary>Pure scalar helpers shared across the engine. No allocation, no state.</summary>
public static class MathUtil
{
    /// <summary>Clamp to [0, 1].</summary>
    public static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>Linear interpolation; <paramref name="t"/> is not clamped.</summary>
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Inverse of <see cref="Lerp"/>: where does <paramref name="v"/> sit in [a, b]? Degenerate (a == b) returns 0.</summary>
    public static float InverseLerp(float a, float b, float v) => a == b ? 0f : (v - a) / (b - a);

    /// <summary>Clamped Hermite smoothstep: 0 at x&lt;=a, 1 at x&gt;=b, smooth in between. Returns 0.5 at the midpoint.</summary>
    public static float SmoothStep(float a, float b, float x)
    {
        if (a == b) return x < a ? 0f : 1f;
        float t = System.Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>
    /// Normalize an angle in RADIANS to the half-open interval (-pi, pi]: exactly -pi comes back as +pi,
    /// exactly +pi stays put. This is what keeps a yaw accumulated over many frames from growing without
    /// bound, and it is the interval every other helper here returns in.
    /// </summary>
    public static float WrapAngle(float radians)
    {
        float a = radians % System.MathF.Tau;
        if (a > System.MathF.PI) a -= System.MathF.Tau;
        else if (a <= -System.MathF.PI) a += System.MathF.Tau;
        return a;
    }

    /// <summary>
    /// The SHORTEST signed rotation in radians that takes <paramref name="from"/> to <paramref name="to"/>,
    /// in (-pi, pi]. Turning from 350 degrees to 10 degrees is +20 degrees, not -340. The two exact
    /// opposites resolve to +pi (the interval's closed end), never -pi.
    /// </summary>
    public static float DeltaAngle(float from, float to) => WrapAngle(to - from);

    /// <summary>
    /// Step <paramref name="current"/> toward <paramref name="target"/> by at most
    /// <paramref name="maxDelta"/> radians, along the shortest arc, and wrap the result to (-pi, pi].
    /// This is the frame-rate-independent "turn toward a heading at a bounded rate": pass
    /// <c>maxTurnRate * dt</c>. Never overshoots. A non-positive <paramref name="maxDelta"/> is a zero
    /// step, so it holds the current heading (wrapped), it does not snap to the target.
    /// </summary>
    public static float MoveTowardsAngle(float current, float target, float maxDelta)
    {
        if (maxDelta <= 0f) return WrapAngle(current);
        float delta = DeltaAngle(current, target);
        if (delta > maxDelta) delta = maxDelta;
        else if (delta < -maxDelta) delta = -maxDelta;
        return WrapAngle(current + delta);
    }

    /// <summary>
    /// Interpolate from <paramref name="a"/> to <paramref name="b"/> along the SHORTEST arc, result
    /// wrapped to (-pi, pi]. Halfway between 350 and 10 degrees is 0, not 180. <paramref name="t"/> is
    /// not clamped, matching <see cref="Lerp"/>.
    /// </summary>
    public static float LerpAngle(float a, float b, float t) => WrapAngle(a + DeltaAngle(a, b) * t);
}
