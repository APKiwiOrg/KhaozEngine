namespace KhaozEngine.Render3D;

/// <summary>Whether a rigid caster belongs to cached static point-shadow depth or current-frame depth.</summary>
public enum ShadowCasterMobility : byte
{
    /// <summary>Use the cached rigid signature, invalidating it when this caster changes.</summary>
    Static = 0,

    /// <summary>Render current geometry outside the cached static point-shadow signature.</summary>
    Dynamic = 1,
}
