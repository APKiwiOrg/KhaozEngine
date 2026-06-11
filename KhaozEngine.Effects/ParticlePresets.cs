using Microsoft.Xna.Framework;

namespace KhaozEngine.Effects;

/// <summary>Built-in emitter presets. <see cref="Spark"/> and <see cref="Ember"/> reproduce Nullwake's hit effects.</summary>
public static class ParticlePresets
{
    /// <summary>Fast outward spark burst, lightened toward white. Nullwake mining-hit look.</summary>
    public static readonly ParticleEmitterConfig Spark = new()
    {
        MinSpeed = 40f,
        MaxSpeed = 80f,
        MinLife = 0.22f,
        MaxLife = 0.35f,
        StartSize = 2f,
        EndSizeFactor = 1f,
        Emission = ParticleEmission.Radial,
        JitterX = 3f,
        JitterY = 3f,
        BlendTarget = Color.White,
        BlendAmount = 0.5f,
    };

    /// <summary>Slow upward-drifting embers with horizontal sway. Nullwake damage-over-time look.</summary>
    public static readonly ParticleEmitterConfig Ember = new()
    {
        MinSpeed = 15f,
        MaxSpeed = 25f,
        MinLife = 0.45f,
        MaxLife = 0.7f,
        StartSize = 3f,
        EndSizeFactor = 0.3f,
        Emission = ParticleEmission.Directional,
        Direction = new Vector2(0f, -1f),
        SpreadRadians = 0f,
        JitterX = 5f,
        JitterY = 3f,
        SwayFrequency = 6f,
        SwayAmplitude = 8f,
        OverrideColor = new Color(255, 160, 40),
    };
}
