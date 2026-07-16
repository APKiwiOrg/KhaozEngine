using System;

namespace KhaozEngine.Particles;

/// <summary>
/// The remap family a <see cref="ParticleCurve"/> can apply. <see cref="Linear"/> (value 0) is the default
/// so <c>default(ParticleCurve)</c> reproduces the legacy straight start-to-end lerp exactly.
/// </summary>
public enum ParticleCurveKind : byte
{
    /// <summary>Identity: <c>Evaluate(n) == n</c>. The default and the bit-identical legacy path.</summary>
    Linear = 0,

    /// <summary>Accelerating ease (<c>n*n</c>): slow to start, fast at the end.</summary>
    EaseIn,

    /// <summary>Decelerating ease (<c>1 - (1-n)^2</c>): fast to start, slow at the end.</summary>
    EaseOut,

    /// <summary>Smoothstep ease in and out (<c>n*n*(3-2n)</c>).</summary>
    EaseInOut,

    /// <summary>Snaps to End at birth, hits Start at the peak time (<c>Param</c>), decays back to End.</summary>
    Flash,

    /// <summary>Trapezoid remap: 1 at birth and death, ramping to 0 across the middle plateau over the edge
    /// fraction (<c>Param</c>). A transparent End with a visible Start reads as fade-in, hold, fade-out.</summary>
    FadeInOut,

    /// <summary>Cosine pulse of <c>Param</c> cycles across the lifetime (0 to 1 and back, repeated).</summary>
    Pulse,
}

/// <summary>
/// A cheap value-type remap applied to a particle's normalised age before the Start/End lerp. A particle
/// value is always <c>lerp(startValue, endValue, curve.Evaluate(n))</c>, so <see cref="ParticleCurveKind.Linear"/>
/// (the default) is bit-identical to the legacy straight interpolation.
/// </summary>
public readonly struct ParticleCurve
{
    /// <summary>The remap family. Defaults to <see cref="ParticleCurveKind.Linear"/>.</summary>
    public ParticleCurveKind Kind { get; }

    /// <summary>Kind-specific shaping parameter (peak time, edge fraction, cycle count). Zero picks a sane default.</summary>
    public float Param { get; }

    /// <summary>Build a curve of the given kind and optional shaping parameter.</summary>
    public ParticleCurve(ParticleCurveKind kind, float param = 0f)
    {
        Kind = kind;
        Param = param;
    }

    /// <summary>Remap a normalised age <paramref name="n"/> in [0,1] to a lerp position in [0,1].</summary>
    public float Evaluate(float n)
    {
        switch (Kind)
        {
            case ParticleCurveKind.EaseIn:
                return n * n;
            case ParticleCurveKind.EaseOut:
                return 1f - (1f - n) * (1f - n);
            case ParticleCurveKind.EaseInOut:
                return S(n);
            case ParticleCurveKind.Flash:
            {
                float p = Param <= 0f ? 0.15f : Param;
                return n < p ? 1f - S(n / p) : S((n - p) / (1f - p));
            }
            case ParticleCurveKind.FadeInOut:
            {
                float e = Param <= 0f ? 0.2f : Param;
                return 1f - Math.Clamp(MathF.Min(n, 1f - n) / e, 0f, 1f);
            }
            case ParticleCurveKind.Pulse:
            {
                float c = Param <= 0f ? 2f : Param;
                return 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * c * n);
            }
            default:
                return n;
        }
    }

    /// <summary>Hermite smoothstep <c>x*x*(3-2x)</c>, the shared building block for the eased kinds.</summary>
    private static float S(float x) => x * x * (3f - 2f * x);

    /// <summary>Identity remap (legacy straight lerp).</summary>
    public static ParticleCurve Linear => new(ParticleCurveKind.Linear);

    /// <summary>Accelerating ease.</summary>
    public static ParticleCurve EaseIn => new(ParticleCurveKind.EaseIn);

    /// <summary>Decelerating ease.</summary>
    public static ParticleCurve EaseOut => new(ParticleCurveKind.EaseOut);

    /// <summary>Smoothstep ease in and out.</summary>
    public static ParticleCurve EaseInOut => new(ParticleCurveKind.EaseInOut);

    /// <summary>A fast snap to Start then decay back to End, peaking at <paramref name="peakTime"/> in [0,1].</summary>
    public static ParticleCurve Flash(float peakTime = 0.15f) => new(ParticleCurveKind.Flash, peakTime);

    /// <summary>A trapezoid remap that returns 1 at the edges and 0 across the middle plateau, ramping over
    /// <paramref name="edge"/> (fraction of the lifetime).</summary>
    public static ParticleCurve FadeInOut(float edge = 0.2f) => new(ParticleCurveKind.FadeInOut, edge);

    /// <summary>A cosine pulse of <paramref name="cycles"/> full cycles across the lifetime.</summary>
    public static ParticleCurve Pulse(float cycles = 2f) => new(ParticleCurveKind.Pulse, cycles);
}
