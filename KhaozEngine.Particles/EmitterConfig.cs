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
