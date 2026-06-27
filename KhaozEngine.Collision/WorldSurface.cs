using System;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// A placed prop surface: a unit <see cref="PropSurface"/> positioned at a world XZ <see cref="Center"/>, scaled by
/// <see cref="Scale"/>, rotated by <see cref="Yaw"/>, sitting at <see cref="BaseY"/>. <see cref="SampleWorld"/>
/// transforms a world (x,z) into the prop's local frame, samples the grid, and scales the height back to world -
/// the same transform-at-query the colliders use, so it is identical on client and server.
/// </summary>
public readonly struct WorldSurface
{
    /// <summary>The unit (unscaled) height grid.</summary>
    public PropSurface Surface { get; }
    /// <summary>World XZ centre (the placement point).</summary>
    public Vector2 Center { get; }
    /// <summary>Per-instance uniform scale.</summary>
    public float Scale { get; }
    /// <summary>Per-instance yaw (radians).</summary>
    public float Yaw { get; }
    /// <summary>World Y of the prop's base (its feet).</summary>
    public float BaseY { get; }

    public WorldSurface(PropSurface surface, Vector2 center, float scale, float yaw, float baseY)
    {
        Surface = surface ?? throw new ArgumentNullException(nameof(surface));
        Center = center; Scale = scale <= 0f ? 1f : scale; Yaw = yaw; BaseY = baseY;
    }

    /// <summary>Conservative broad-phase radius: the unit grid's half-diagonal times the scale.</summary>
    public float BoundingRadius
    {
        get
        {
            float hx = MathF.Max(MathF.Abs(Surface.OriginX), MathF.Abs(Surface.OriginX + Surface.Width * Surface.CellSize));
            float hz = MathF.Max(MathF.Abs(Surface.OriginZ), MathF.Abs(Surface.OriginZ + Surface.Height * Surface.CellSize));
            return MathF.Sqrt(hx * hx + hz * hz) * Scale;
        }
    }

    /// <summary>The world top height of this placed surface (base + scaled max), used as the collider top.</summary>
    public float TopWorld => BaseY + Surface.MaxHeight * Scale;

    /// <summary>The world top height under (x, z), or null when (x, z) is outside this prop's footprint.</summary>
    public float? SampleWorld(float x, float z)
    {
        // World -> local: translate, rotate by -yaw, unscale.
        float dx = x - Center.X, dz = z - Center.Y;
        float cos = MathF.Cos(Yaw), sin = MathF.Sin(Yaw);
        float lx = (dx * cos + dz * sin) / Scale;
        float lz = (-dx * sin + dz * cos) / Scale;
        float? h = Surface.SampleLocal(lx, lz);
        return h.HasValue ? h.Value * Scale + BaseY : (float?)null;
    }
}
