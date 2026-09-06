using System;
using System.Numerics;

namespace KhaozEngine.Render3D;

/// <summary>A cosmetic world-space influence that bends nearby foliage without adding collision.</summary>
public readonly record struct FoliageInteractor(Vector3 Position, float Radius, float Strength = 1f)
{
    /// <summary>Rejects non-finite positions, negative radii and strengths outside the range 0 through 1.</summary>
    public void Validate()
    {
        if (!float.IsFinite(Position.X) || !float.IsFinite(Position.Y) || !float.IsFinite(Position.Z))
            throw new ArgumentException("Foliage interactor position must be finite.", nameof(Position));
        if (!float.IsFinite(Radius) || Radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(Radius), "Foliage interactor radius must be finite and non-negative.");
        if (!float.IsFinite(Strength) || Strength < 0f || Strength > 1f)
            throw new ArgumentOutOfRangeException(nameof(Strength), "Foliage interactor strength must be from 0 through 1.");
    }
}
