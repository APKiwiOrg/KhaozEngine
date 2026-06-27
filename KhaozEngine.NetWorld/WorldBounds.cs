using System;
using System.Numerics;

namespace KhaozEngine.NetWorld;

/// <summary>
/// An authoritative play-area shape the movement step clamps to each tick, so a player cannot be pushed
/// (or glitch) outside the bounded region even where the diegetic <c>RimFeature</c> wall could be climbed.
/// <see cref="Clamp"/> returns the nearest in-bounds point: a no-op inside (idempotent) and a projection
/// onto the boundary outside, which produces clamp-and-slide when applied every tick (the tangential part
/// of a blocked move survives). Nullable at the call site - no bounds means unbounded movement.
/// </summary>
public abstract class WorldBounds
{
    /// <summary>True when (x, z) is inside or on the boundary.</summary>
    public abstract bool Contains(float x, float z);

    /// <summary>The nearest point inside-or-on the bounds; (x, z) itself when already inside.</summary>
    public abstract Vector2 Clamp(float x, float z);
}

/// <summary>A circular play area centred at <see cref="Center"/> with radius <see cref="Radius"/>.</summary>
public sealed class CircleBounds : WorldBounds
{
    public CircleBounds(Vector2 center, float radius)
    {
        Center = center;
        Radius = MathF.Max(0f, radius);
    }

    public Vector2 Center { get; }
    public float Radius { get; }

    public override bool Contains(float x, float z)
    {
        float dx = x - Center.X, dz = z - Center.Y;
        return dx * dx + dz * dz <= Radius * Radius;
    }

    public override Vector2 Clamp(float x, float z)
    {
        float dx = x - Center.X, dz = z - Center.Y;
        float d2 = dx * dx + dz * dz;
        if (d2 <= Radius * Radius) return new Vector2(x, z);
        float d = MathF.Sqrt(d2);
        if (d < 1e-6f) return Center;
        float s = Radius / d;
        return new Vector2(Center.X + dx * s, Center.Y + dz * s);
    }
}

/// <summary>An axis-aligned rectangular play area (XZ).</summary>
public sealed class RectBounds : WorldBounds
{
    public RectBounds(float minX, float minZ, float maxX, float maxZ)
    {
        MinX = MathF.Min(minX, maxX);
        MaxX = MathF.Max(minX, maxX);
        MinZ = MathF.Min(minZ, maxZ);
        MaxZ = MathF.Max(minZ, maxZ);
    }

    public float MinX { get; }
    public float MinZ { get; }
    public float MaxX { get; }
    public float MaxZ { get; }

    public override bool Contains(float x, float z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;

    public override Vector2 Clamp(float x, float z) =>
        new(Math.Clamp(x, MinX, MaxX), Math.Clamp(z, MinZ, MaxZ));
}
