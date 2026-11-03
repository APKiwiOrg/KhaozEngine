using System;

namespace KhaozEngine.Render3D;

/// <summary>Private probe switches. Each skips arithmetic whose result is already known exactly, so a frame's output
/// bits should not change. Only the frame recorder's sampling window turns one on. The values are the mask the recorder
/// schedules and reports.</summary>
[Flags]
public enum ShaderGateProbe
{
    None = 0,
    /// <summary>Skip a point light's shadow filter on a fragment whose banded N dot L is exactly zero.</summary>
    PointShadowZeroNdl = 1,
    /// <summary>Stop the underwater shadow march once its running visibility is exactly positive zero.</summary>
    UnderwaterMarchZero = 2,
    /// <summary>Reuse the current world position as the previous one on a rigid instance whose transform did not change.</summary>
    RigidWorldReuse = 4,
    /// <summary>Reuse the converted internal Lanczos pair only at exactly equal internal and display sizes.</summary>
    LanczosExactSizeReuse = 8,
    /// <summary>Skip specular vectors and powers only at exactly zero specular strength.</summary>
    ZeroSpecular = 16,
    /// <summary>Reuse a material evaluation for a repeated tile slot, preserving every weighted addition.</summary>
    TileSlotReuse = 32,
}
