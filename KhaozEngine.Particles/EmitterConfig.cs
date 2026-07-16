using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.Particles;

/// <summary>
/// Spawn parameters for a burst/emitter. The size/colour endpoints, gravity and drag are baked into each
/// particle at <see cref="ParticleSystem.Emit"/> time, so the config can be discarded after the call.
/// </summary>
public struct EmitterConfig
{
    /// <summary>Minimum particle lifetime in seconds.</summary>
    public float LifetimeMin;

    /// <summary>Maximum particle lifetime in seconds.</summary>
    public float LifetimeMax;

    /// <summary>Minimum initial speed along the spread cone.</summary>
    public float SpeedMin;

    /// <summary>Maximum initial speed along the spread cone.</summary>
    public float SpeedMax;

    /// <summary>Cone axis (normalised internally; a ~zero vector emits omnidirectionally).</summary>
    public Vector3 Direction;

    /// <summary>Cone half-angle in degrees (0 = straight, 180 = full sphere).</summary>
    public float SpreadDegrees;

    /// <summary>World acceleration applied each second.</summary>
    public Vector3 Gravity;

    /// <summary>Velocity damping per second (0 = none).</summary>
    public float Drag;

    /// <summary>Size at spawn (lerped to <see cref="EndSize"/> over the particle's normalised age).</summary>
    public float StartSize;

    /// <summary>Size at death.</summary>
    public float EndSize;

    /// <summary>RGBA colour at spawn (lerped to <see cref="EndColor"/> over normalised age; alpha too).</summary>
    public Color StartColor;

    /// <summary>RGBA colour at death (set alpha to 0 to fade out).</summary>
    public Color EndColor;

    // --- Modernisation fields (all zero-default = exactly the legacy behaviour above) ---

    /// <summary>Spawn volume around the origin. Default <see cref="EmissionShape.Point"/> spawns at the origin.</summary>
    public EmissionShape Shape;

    /// <summary>Radius of the spawn <see cref="Shape"/> (world units). Ignored for <see cref="EmissionShape.Point"/>.</summary>
    public float ShapeRadius;

    /// <summary>0 fills the shape volume, 1 spawns only on its surface/edge (a shell). Blends in between.</summary>
    public float ShapeShell;

    /// <summary>How the initial velocity direction is chosen. Default <see cref="ParticleVelocityMode.Cone"/>.</summary>
    public ParticleVelocityMode VelocityMode;

    /// <summary>Per-particle size jitter in 0..1: bakes a multiplier <c>1 + SizeVariance*(2u-1)</c> into both
    /// start and end size. 0 disables the jitter (and its RNG draw).</summary>
    public float SizeVariance;

    /// <summary>When true, each particle blends a random <c>t</c> between the A and B colour pairs at spawn
    /// (<see cref="StartColor"/>/<see cref="StartColorB"/> and <see cref="EndColor"/>/<see cref="EndColorB"/>).</summary>
    public bool VaryColor;

    /// <summary>Second start colour for the <see cref="VaryColor"/> random-between-two-gradients blend.</summary>
    public Color StartColorB;

    /// <summary>Second end colour for the <see cref="VaryColor"/> random-between-two-gradients blend.</summary>
    public Color EndColorB;

    /// <summary>When true, colour interpolates through <see cref="MidColor"/> at normalised age 0.5 (3-stop gradient).</summary>
    public bool UseMidColor;

    /// <summary>The middle colour stop used when <see cref="UseMidColor"/> is set.</summary>
    public Color MidColor;

    /// <summary>Remaps the normalised age fed to the size lerp. Default (<see cref="ParticleCurveKind.Linear"/>) is
    /// bit-identical to the legacy straight interpolation.</summary>
    public ParticleCurve SizeCurve;

    /// <summary>Remaps the normalised age fed to the alpha lerp. Default (<see cref="ParticleCurveKind.Linear"/>) is
    /// bit-identical to the legacy straight interpolation.</summary>
    public ParticleCurve AlphaCurve;

    /// <summary>Minimum spin rate in rad/s (negatives allowed). Draws a per-particle spin when either bound is nonzero.</summary>
    public float SpinMin;

    /// <summary>Maximum spin rate in rad/s (negatives allowed).</summary>
    public float SpinMax;

    /// <summary>When true, each particle is given a random initial <see cref="Particle.Rotation"/> in [0, 2pi).</summary>
    public bool RandomStartRotation;

    /// <summary>Strength of the deterministic curl-noise turbulence force. 0 disables it (no per-frame noise work).</summary>
    public float TurbulenceStrength;

    /// <summary>Spatial/temporal frequency of the turbulence field. &lt;= 0 is treated as 1.</summary>
    public float TurbulenceFrequency;

    /// <summary>A short-lived, fast, fading spark (good additive hit/muzzle burst).</summary>
    public static EmitterConfig Spark => new()
    {
        LifetimeMin = 0.20f,
        LifetimeMax = 0.45f,
        SpeedMin = 4.0f,
        SpeedMax = 9.0f,
        Direction = Vector3.Zero, // omni
        SpreadDegrees = 180f,
        Gravity = new Vector3(0f, -6f, 0f),
        Drag = 2.0f,
        StartSize = 0.25f,
        EndSize = 0.05f,
        StartColor = new Color(1.0f, 0.85f, 0.4f, 1.0f),
        EndColor = new Color(1.0f, 0.3f, 0.1f, 0.0f),
    };

    /// <summary>A slow, growing, fading smoke-ish puff.</summary>
    public static EmitterConfig Puff => new()
    {
        LifetimeMin = 0.8f,
        LifetimeMax = 1.6f,
        SpeedMin = 0.3f,
        SpeedMax = 1.2f,
        Direction = new Vector3(0f, 1f, 0f),
        SpreadDegrees = 35f,
        Gravity = new Vector3(0f, 0.5f, 0f),
        Drag = 1.0f,
        StartSize = 0.3f,
        EndSize = 1.1f,
        StartColor = new Color(0.7f, 0.7f, 0.72f, 0.6f),
        EndColor = new Color(0.4f, 0.4f, 0.42f, 0.0f),
    };
}
