using System;
using KhaozEngine.MapDoc;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEditor;

/// <summary>GPU-free geometry + editing helpers for terrain features, shared by the overlay marker draw, the
/// overlay pick, the transform gizmo, and the inspector so all four agree on a feature's center, on how a feature
/// clones, and on how a move / scale gesture rewrites it. Generic over the four built-ins (lake, flatten, ridge,
/// rim); an unknown custom feature type has no known center or default form, so those helpers return
/// <c>false</c> / <c>null</c> rather than guessing by reflection.</summary>
internal static class FeatureGeometry
{
    /// <summary>The XZ center of a feature: lake / flatten / rim expose CenterX/CenterZ, ridge exposes its
    /// PointX/PointZ. Returns false (and a zero center) for an unknown custom type.</summary>
    internal static bool TryCenter(MapFeature feature, out float x, out float z)
    {
        switch (feature)
        {
            case LakeFeatureDoc l: x = l.CenterX; z = l.CenterZ; return true;
            case FlattenFeatureDoc f: x = f.CenterX; z = f.CenterZ; return true;
            case RimFeatureDoc r: x = r.CenterX; z = r.CenterZ; return true;
            case RidgeFeatureDoc r: x = r.PointX; z = r.PointZ; return true;
            default: x = 0f; z = 0f; return false;
        }
    }

    /// <summary>Margin (world units) padded around a feature's height-change reach so the footprint also covers
    /// every terrain vertex whose NORMAL shifts, not just those whose height moves. A chunk vertex reads its normal
    /// from a central finite difference in <see cref="Terrain.TerrainField.SampleNormal"/> at eps = 1 m, so a vertex
    /// up to 1 m outside the reach still re-normals, and the coarsest chunk LOD samples at
    /// <c>ChunkSize (60) / 16 = 3.75 m</c> spacing. That puts the floor at 1 + 3.75 = 4.75 m, rounded up to 8 m for
    /// headroom so a chunk merely straddling the footprint edge is still caught. Deliberately generous: an over-wide
    /// footprint only rebuilds a couple of extra loaded chunks, while an under-wide one leaves stale terrain, so the
    /// asymmetry favours padding. A hardcoded constant (not a reference to the Terrain.Render3D chunk/LOD sizes)
    /// keeps this GPU-free geometry helper free of a render dependency.</summary>
    internal const float FootprintMargin = 8f;

    /// <summary>A conservative world-space AABB covering everywhere a built-in feature can change terrain height,
    /// padded by <see cref="FootprintMargin"/>. This is the dirty region a feature edit invalidates instead of
    /// rebuilding the whole streamed world. Returns false (and a default area) for the ridge and any unknown custom
    /// type, the same no-guessing rule <see cref="TryCenter"/> follows: only a feature whose reach is a known
    /// bounded disc yields a footprint. Per built-in, reach derived from each runtime feature's <c>Apply</c>:
    /// <list type="bullet">
    /// <item>Lake: a disc of <c>Radius * OuterFraction</c>. <see cref="Terrain.LakeFeature.Apply"/> fades the carve
    /// to zero at <c>radius * outerFraction</c> (outerFraction defaults to 1.30 and is author-settable), so the
    /// true reach is <c>radius * outerFraction</c>, NOT radius.</item>
    /// <item>Flatten: a disc of <c>Radius</c>. <see cref="Terrain.FlattenFeature.Apply"/> fades to no effect by
    /// <c>radius</c> (Blend only moves the inner full-effect edge in, never out), so radius is the exact outer
    /// reach.</item>
    /// <item>Rim: a disc of <c>OuterRadius</c>. <see cref="Terrain.RimFeature.Apply"/> ramps the wall up by
    /// <c>OuterRadius</c>. Ruggedness scales wall HEIGHT, not horizontal reach, so it adds no excursion beyond
    /// OuterRadius. The wall holds a plateau beyond OuterRadius, but the rim is the world-edge wall (its OuterRadius
    /// sits at or beyond the streamed extent), so OuterRadius bounds the change the editor can see.</item>
    /// <item>Ridge: false. A ridge is an unbounded half-plane band along its direction (no finite AABB).</item>
    /// </list></summary>
    internal static bool TryFootprint(MapFeature feature, out RectArea area)
    {
        switch (feature)
        {
            case LakeFeatureDoc l: area = DiscFootprint(l.CenterX, l.CenterZ, l.Radius * l.OuterFraction); return true;
            case FlattenFeatureDoc f: area = DiscFootprint(f.CenterX, f.CenterZ, f.Radius); return true;
            case RimFeatureDoc r: area = DiscFootprint(r.CenterX, r.CenterZ, r.OuterRadius); return true;
            default: area = default; return false;   // ridge (unbounded band) and every unknown custom type
        }
    }

    // The AABB of a disc of |reach| around (cx, cz), padded by FootprintMargin. The abs guards a degenerate
    // negative reach (e.g. a bad scale factor) from producing an inverted (min > max) area.
    static RectArea DiscFootprint(float cx, float cz, float reach)
    {
        float r = MathF.Abs(reach) + FootprintMargin;
        return new RectArea(cx - r, cz - r, cx + r, cz + r);
    }

    /// <summary>The smallest axis-aligned rect covering both <paramref name="a"/> and <paramref name="b"/> (min of
    /// the mins, max of the maxes). Used to union two feature footprints (an edit's old + new endpoints) and to
    /// accumulate a pending dirty region across several edits.</summary>
    internal static RectArea Union(RectArea a, RectArea b) => new(
        MathF.Min(a.MinX, b.MinX), MathF.Min(a.MinZ, b.MinZ),
        MathF.Max(a.MaxX, b.MaxX), MathF.Max(a.MaxZ, b.MaxZ));

    /// <summary>Deep-copies one of the four built-in feature DTOs so an edit replaces the instance
    /// (<see cref="EditFeatureCommand"/> holds old + new by reference). Throws for a type it cannot clone: only the
    /// four built-ins are ever passed here.</summary>
    internal static MapFeature Clone(MapFeature feature) => feature switch
    {
        LakeFeatureDoc l => new LakeFeatureDoc
        {
            Name = l.Name, CenterX = l.CenterX, CenterZ = l.CenterZ, Radius = l.Radius, Depth = l.Depth,
            InnerFraction = l.InnerFraction, OuterFraction = l.OuterFraction,
        },
        FlattenFeatureDoc f => new FlattenFeatureDoc
        {
            Name = f.Name, CenterX = f.CenterX, CenterZ = f.CenterZ, Radius = f.Radius,
            TargetHeight = f.TargetHeight, Blend = f.Blend,
        },
        RidgeFeatureDoc r => new RidgeFeatureDoc
        {
            Name = r.Name, PointX = r.PointX, PointZ = r.PointZ, DirectionX = r.DirectionX, DirectionZ = r.DirectionZ,
            Height = r.Height, Width = r.Width, PassAlong = r.PassAlong, PassWidth = r.PassWidth,
        },
        RimFeatureDoc rim => CloneRim(rim),
        _ => throw new InvalidOperationException($"No clone support for feature type '{feature.Type}'."),
    };

    static RimFeatureDoc CloneRim(RimFeatureDoc r)
    {
        var clone = new RimFeatureDoc
        {
            Name = r.Name, CenterX = r.CenterX, CenterZ = r.CenterZ, InnerRadius = r.InnerRadius, OuterRadius = r.OuterRadius,
            WallHeight = r.WallHeight, Ruggedness = r.Ruggedness, Seed = r.Seed, CrestFrequency = r.CrestFrequency,
        };
        foreach (RimPassDoc pass in r.Passes)
            clone.Passes.Add(new RimPassDoc { AngleRadians = pass.AngleRadians, HalfWidth = pass.HalfWidth, Falloff = pass.Falloff });
        return clone;
    }

    /// <summary>A clone of <paramref name="start"/> translated by (<paramref name="dx"/>, <paramref name="dz"/>) on
    /// the XZ plane: lake / flatten / rim shift their center, ridge shifts its through-point. Null for an unknown
    /// custom type (the gizmo does not move it). Every other field carries over from the clone.</summary>
    internal static MapFeature? Translated(MapFeature start, float dx, float dz)
    {
        switch (start)
        {
            case LakeFeatureDoc l: { var c = (LakeFeatureDoc)Clone(l); c.CenterX += dx; c.CenterZ += dz; return c; }
            case FlattenFeatureDoc f: { var c = (FlattenFeatureDoc)Clone(f); c.CenterX += dx; c.CenterZ += dz; return c; }
            case RimFeatureDoc r: { var c = (RimFeatureDoc)Clone(r); c.CenterX += dx; c.CenterZ += dz; return c; }
            case RidgeFeatureDoc r: { var c = (RidgeFeatureDoc)Clone(r); c.PointX += dx; c.PointZ += dz; return c; }
            default: return null;
        }
    }

    /// <summary>A clone of <paramref name="start"/> with its primary radius field scaled by
    /// <paramref name="factor"/>: lake Radius, flatten Radius, rim Inner + Outer together, ridge Width. Null for an
    /// unknown custom type (the gizmo does not resize it). Every other field carries over from the clone.</summary>
    internal static MapFeature? Scaled(MapFeature start, float factor)
    {
        switch (start)
        {
            case LakeFeatureDoc l: { var c = (LakeFeatureDoc)Clone(l); c.Radius *= factor; return c; }
            case FlattenFeatureDoc f: { var c = (FlattenFeatureDoc)Clone(f); c.Radius *= factor; return c; }
            case RimFeatureDoc r: { var c = (RimFeatureDoc)Clone(r); c.InnerRadius *= factor; c.OuterRadius *= factor; return c; }
            case RidgeFeatureDoc r: { var c = (RidgeFeatureDoc)Clone(r); c.Width *= factor; return c; }
            default: return null;
        }
    }

    /// <summary>A clone of <paramref name="start"/> rotated on the XZ plane by <paramref name="deltaRadians"/>: a
    /// ridge turns its direction unit vector (standard atan2-increasing rotation, renormalized, a degenerate zero
    /// direction left as-is), a rim adds the delta to every pass's angle (wrapped to the canonical range). Null for
    /// lake / flatten (rotationally symmetric), a rim with zero passes (also rotationally symmetric, nothing to
    /// rotate), and any unknown custom type, so the gizmo offers no yaw ring and a ring grab cannot arm where there
    /// is no orientation to turn. Every other field carries over from the clone.</summary>
    internal static MapFeature? Rotated(MapFeature start, float deltaRadians)
    {
        switch (start)
        {
            case RidgeFeatureDoc r:
            {
                var c = (RidgeFeatureDoc)Clone(r);
                float cos = MathF.Cos(deltaRadians), sin = MathF.Sin(deltaRadians);
                float nx = r.DirectionX * cos - r.DirectionZ * sin;
                float nz = r.DirectionX * sin + r.DirectionZ * cos;
                float len = MathF.Sqrt(nx * nx + nz * nz);
                if (len < 1e-6f) return c;   // degenerate zero direction: nothing to rotate, keep the clone's carried value
                c.DirectionX = nx / len;
                c.DirectionZ = nz / len;
                return c;
            }
            case RimFeatureDoc rim when rim.Passes.Count > 0:
            {
                var c = (RimFeatureDoc)Clone(rim);
                foreach (RimPassDoc pass in c.Passes)
                    pass.AngleRadians = WrapToPi(pass.AngleRadians + deltaRadians);
                return c;
            }
            default: return null;
        }
    }

    // Shortest signed wrap of an angle to (-pi, pi], the same idiom GizmoDrag uses so a rotated pass angle stays canonical.
    static float WrapToPi(float a) => MathF.Atan2(MathF.Sin(a), MathF.Cos(a));

    /// <summary>A default-parameterized feature of <paramref name="type"/> centered at (<paramref name="x"/>,
    /// <paramref name="z"/>), for the click-place tool: a lake (r10, d3), a flatten (r10, target at
    /// <paramref name="groundHeight"/>), a ridge through the point, or a rim centered there. Null for a type
    /// outside the four built-ins (a game's custom type has no editor default), so a click on such a selection
    /// places nothing.</summary>
    internal static MapFeature? CreateDefault(string type, float x, float z, float groundHeight) => type switch
    {
        "lake" => new LakeFeatureDoc { CenterX = x, CenterZ = z, Radius = 10f, Depth = 3f },
        "flatten" => new FlattenFeatureDoc { CenterX = x, CenterZ = z, Radius = 10f, TargetHeight = groundHeight },
        "ridge" => new RidgeFeatureDoc { PointX = x, PointZ = z, Height = 5f, Width = 10f },
        "rim" => new RimFeatureDoc { CenterX = x, CenterZ = z, InnerRadius = 10f, OuterRadius = 14f, WallHeight = 6f },
        _ => null,
    };
}
