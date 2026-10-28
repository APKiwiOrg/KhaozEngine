using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfPoolPropKind { Rock, Caster, Floater }

/// <summary>An axis-aligned pool prop. <see cref="Size"/> is its full extent on each axis.</summary>
internal sealed record RendererPerfPoolProp(RendererPerfPoolPropKind Kind, Vector3 Centre, Vector3 Size)
{
    internal Vector3 Min => Centre - Size * .5f;
    internal Vector3 Max => Centre + Size * .5f;
}

/// <summary>One static water optics pool: a bounded water plane over a sloping bed, with submerged rocks resting
/// on the bed, wholly submerged casters and floaters straddling the surface. Every prop casts shadows.</summary>
internal sealed class RendererPerfWaterOpticsPool
{
    internal const float SurfaceY = .25f, FloorY = -1.25f, ShoreBedY = .05f, WaveMargin = .15f;
    internal const int BedQuads = 16, RockCount = 24, CasterCount = 12, FloaterCount = 12;
    // Vertical gap kept between a prop and the bed or the clearance line, and horizontal gap between props.
    const float Gap = .05f, BaseSpacing = .3f;
    // Props stay inside this fraction of the first frame, so the small per-frame camera drift keeps them in view.
    const float FrameLimit = .9f;
    // Unscaled prop extents. A pool scales them with its prop scale.
    static readonly Vector3 RockSize = new(1f, .3f, .8f), CasterSize = new(1f, .3f, 1f), FloaterFootprint = new(1f, 0f, 1f);
    static readonly Color BedTint = new(.45f, .4f, .3f, 1f), RockTint = new(.4f, .38f, .35f, 1f),
        CasterTint = new(.6f, .45f, .3f, 1f), FloaterTint = new(.7f, .65f, .55f, 1f);
    // The v1 village prop meshes. Rocks are flattened spheres, casters boxes and floaters rounded boxes.
    readonly GltfMesh _bed, _rock = MeshPrimitives.Sphere(1.6f, 10, 12), _caster = MeshPrimitives.Box(.8f),
        _floater = MeshPrimitives.RoundedBox(1.4f, .3f, 4);
    readonly uint _seed;
    RendererPerfPoolProp[]? _props;
    RigidInstanceDraw[] _draws = [];
    MeshHandle[] _handles = [];

    internal RendererPerfWaterOpticsPool(float minX, float maxX, float shoreZ, float farZ, float waveBound,
        WaterLook? look, uint seed, float propScale = 1f)
    {
        (MinX, MaxX, ShoreZ, FarZ, WaveBound) = (minX, maxX, shoreZ, farZ, waveBound);
        Clearance = waveBound + WaveMargin;
        Plane = new WaterPlane((minX + maxX) * .5f, SurfaceY, (shoreZ + farZ) * .5f, (maxX - minX) * .5f,
            (farZ - shoreZ) * .5f, look, floorY: FloorY);
        _bed = BuildBed();
        _seed = seed;
        PropScale = propScale;
    }

    /// <summary>Place the props inside the recipe's first frame. A scene's camera targets the pool, so the scene
    /// places once its camera exists.</summary>
    internal void Place(Matrix4x4 frameViewProjection)
    {
        if (_props is not null) throw new InvalidOperationException("The pool props are already placed.");
        _props = Place(_seed, frameViewProjection);
        _draws = [Draw(0, Matrix4x4.Identity, BedTint, false),
            .. _props.Select(prop => Draw(1 + (int)prop.Kind, World(prop), Tint(prop.Kind), true))];
    }

    internal float MinX { get; }
    internal float MaxX { get; }
    internal float ShoreZ { get; }
    internal float FarZ { get; }
    /// <summary>The active wave source's vertical amplitude bound in metres.</summary>
    internal float WaveBound { get; }
    /// <summary>The wave bound plus <see cref="WaveMargin"/>. Casters stay this far below the surface and
    /// floaters reach at least this far above and below it.</summary>
    internal float Clearance { get; }
    internal WaterPlane Plane { get; }
    /// <summary>Scales prop extents and spacing, never the floater height, which the clearance sets.</summary>
    internal float PropScale { get; }
    float Spacing => BaseSpacing * PropScale;
    internal IReadOnlyList<RendererPerfPoolProp> Props =>
        _props ?? throw new InvalidOperationException("Place the pool props first.");
    internal int Draws => _draws.Length;

    /// <summary>The bed height, sloping linearly from <see cref="ShoreBedY"/> at the shore to
    /// <see cref="FloorY"/> at the far edge.</summary>
    internal float BedY(float z) => ShoreBedY + (FloorY - ShoreBedY) * Math.Clamp((z - ShoreZ) / (FarZ - ShoreZ), 0f, 1f);

    /// <summary>The FFT sea state's peak vertical displacement in metres, derived because the engine exposes no
    /// bound. With no onshore focus and no bathymetry the vertex stage sums one tap per cascade at full weight, so
    /// the surface height is a zero-mean Gaussian field whose variance is the summed cascade height variance m0,
    /// the same sum the ocean producer turns into its significant height. The peak is that significant height,
    /// 4 sqrt(m0), a crest four standard deviations high, which a Gaussian sea exceeds at a point with
    /// probability about 3e-5. The 0.15 m margin above it adds several more deviations for these pools.</summary>
    internal static float FftPeakDisplacement(WaterSeaState sea)
    {
        ArgumentNullException.ThrowIfNull(sea);
        if (sea.OnshoreFocusStrength != 0f) throw new InvalidOperationException("Onshore focus reweights the cascade taps.");
        int n = sea.CascadeResolution;
        var h0 = new Vector4[n * n];
        float variance = 0f;
        for (int cascade = 0; cascade < sea.CascadeCount; cascade++)
            variance += OceanSpectrum.BuildInitialSpectrum(sea, cascade, n, h0).HeightVariance;
        return WaterShoaling.SignificantHeight(variance);
    }

    internal object Geometry => new
    {
        Plane,
        WaveBound,
        Clearance,
        Bed = RendererPerfRecipe.MeshFacts(_bed),
        BedGrid = new { Quads = BedQuads, ShoreBedY, FloorY },
        PropScale,
        Meshes = new[] { _rock, _caster, _floater }.Select(RendererPerfRecipe.MeshFacts).ToArray(),
        Props,
        Draws = _draws,
    };

    /// <summary>The bed and every prop with their mesh bounds. None is terrain.</summary>
    internal IEnumerable<RendererPerfRigidBounds> RigidBounds()
    {
        MeshBounds[] bounds = [.. new[] { _bed, _rock, _caster, _floater }.Select(mesh => MeshBounds.FromVertices(mesh.Vertices))];
        return _draws.Select(draw => new RendererPerfRigidBounds(bounds[draw.Mesh.Index], draw.World, false));
    }

    internal void Load(Scene3D scene) =>
        _handles = [scene.LoadMesh(_bed), scene.LoadMesh(_rock), scene.LoadMesh(_caster), scene.LoadMesh(_floater)];

    internal void Queue(Scene3D scene)
    {
        QueueProps(scene);
        scene.DrawWater(Plane);
    }

    internal void QueueProps(Scene3D scene)
    {
        foreach (RigidInstanceDraw draw in _draws) scene.Draw(draw with { Mesh = _handles[draw.Mesh.Index] });
    }

    GltfMesh BuildBed()
    {
        const int side = BedQuads + 1;
        float width = MaxX - MinX, depth = FarZ - ShoreZ;
        // Constant slope dy/dz, so one normal serves every vertex.
        Vector3 normal = Vector3.Normalize(new Vector3(0f, depth, -(FloorY - ShoreBedY)));
        var vertices = new ModelVertex[side * side];
        var indices = new ushort[BedQuads * BedQuads * 6];
        for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                float wx = MinX + width * x / BedQuads, wz = ShoreZ + depth * z / BedQuads;
                vertices[z * side + x] = new ModelVertex(new Vector3(wx, BedY(wz), wz), normal, Vector4.One,
                    new Vector2((float)x / BedQuads, (float)z / BedQuads), new Vector4(1f, 0f, 0f, 1f));
            }
        int next = 0;
        for (int z = 0; z < BedQuads; z++)
            for (int x = 0; x < BedQuads; x++)
            {
                int a = z * side + x, b = a + 1, c = a + side, d = c + 1;
                indices[next++] = (ushort)a; indices[next++] = (ushort)c; indices[next++] = (ushort)b;
                indices[next++] = (ushort)b; indices[next++] = (ushort)c; indices[next++] = (ushort)d;
            }
        return new GltfMesh(vertices, indices);
    }

    // Each kind starts where the bed is low enough for its vertical rule. Positions are drawn from the seed and
    // rejected when they overlap another prop or leave the frame, so every receiver is on screen.
    RendererPerfPoolProp[] Place(uint seed, Matrix4x4 frame)
    {
        var rng = new SplitMix(seed);
        var placed = new List<RendererPerfPoolProp>();
        float top = SurfaceY - Clearance - Gap;
        Vector3 caster = CasterSize * PropScale, rock = RockSize * PropScale;
        Vector3 floater = FloaterFootprint * PropScale + new Vector3(0f, 2f * Clearance + 2f * Gap, 0f);
        Add(RendererPerfPoolPropKind.Caster, CasterCount, caster, top - caster.Y * .5f, caster.Y);
        Add(RendererPerfPoolPropKind.Floater, FloaterCount, floater, SurfaceY, floater.Y);
        Add(RendererPerfPoolPropKind.Rock, RockCount, rock, float.NaN, 0f);
        return [.. placed];

        void Add(RendererPerfPoolPropKind kind, int count, Vector3 size, float centreY, float height)
        {
            // The bed under the footprint must sit below the prop's bottom. A rock rests on the bed instead,
            // so its top must sit below the clearance line.
            float highestBed = kind == RendererPerfPoolPropKind.Rock ? top - size.Y : centreY - height * .5f - Gap;
            float nearZ = ZAtBed(highestBed) + size.Z * .5f + Gap;
            float farZ = FarZ - size.Z * .5f - Spacing;
            float minX = MinX + size.X * .5f + Spacing, maxX = MaxX - size.X * .5f - Spacing;
            if (!(nearZ < farZ)) throw new InvalidOperationException($"No room for {kind} props in the pool.");
            for (int i = 0, attempts = 0; i < count; attempts++)
            {
                if (attempts > 100_000) throw new InvalidOperationException($"Cannot place {count} {kind} props.");
                float x = minX + (maxX - minX) * rng.NextUnit(), z = nearZ + (farZ - nearZ) * rng.NextUnit();
                float y = kind == RendererPerfPoolPropKind.Rock ? BedY(z) + size.Y * .5f : centreY;
                var prop = new RendererPerfPoolProp(kind, new Vector3(x, y, z), size);
                if (!InFrame(prop, frame) || placed.Any(other => Overlaps(prop, other))) continue;
                placed.Add(prop);
                i++;
            }
        }
    }

    static bool InFrame(RendererPerfPoolProp prop, Matrix4x4 frame)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3((corner & 1) == 0 ? prop.Min.X : prop.Max.X, (corner & 2) == 0 ? prop.Min.Y : prop.Max.Y,
                (corner & 4) == 0 ? prop.Min.Z : prop.Max.Z);
            Vector4 clip = Vector4.Transform(new Vector4(point, 1f), frame);
            if (MathF.Abs(clip.X) > FrameLimit || MathF.Abs(clip.Y) > FrameLimit) return false;
        }
        return true;
    }

    float ZAtBed(float y) => ShoreZ + (FarZ - ShoreZ) * Math.Clamp((y - ShoreBedY) / (FloorY - ShoreBedY), 0f, 1f);

    bool Overlaps(RendererPerfPoolProp a, RendererPerfPoolProp b) =>
        a.Min.X < b.Max.X + Spacing && b.Min.X < a.Max.X + Spacing
        && a.Min.Z < b.Max.Z + Spacing && b.Min.Z < a.Max.Z + Spacing;

    Matrix4x4 World(RendererPerfPoolProp prop)
    {
        float mesh = prop.Kind switch
        {
            RendererPerfPoolPropKind.Rock => 3.2f,
            RendererPerfPoolPropKind.Caster => .8f,
            _ => 1.4f,
        };
        return Matrix4x4.CreateScale(prop.Size / mesh) * Matrix4x4.CreateTranslation(prop.Centre);
    }

    static Color Tint(RendererPerfPoolPropKind kind) => kind switch
    {
        RendererPerfPoolPropKind.Rock => RockTint,
        RendererPerfPoolPropKind.Caster => CasterTint,
        _ => FloaterTint,
    };

    static RigidInstanceDraw Draw(int meshIndex, Matrix4x4 world, Color tint, bool castsShadows) =>
        new(new MeshHandle(meshIndex), world)
        {
            Tint = tint,
            Material = new Material(Color.Transparent, 0f, 32f),
            CastsShadows = castsShadows,
            ShadowOnly = false,
            Motion = MotionKey.From(0),
            Dissolve = 0f,
            DissolveEdgeWidth = 0f,
            DissolveEdgeColor = Color.Transparent,
            InvertShadowDissolve = false,
            DissolveComplement = 0f,
            CutInsideInteriors = false,
        };

    /// <summary>Portable seeded positions. Integer mixing only, so every platform draws the same values.</summary>
    struct SplitMix(uint seed)
    {
        ulong _state = seed;
        internal float NextUnit()
        {
            ulong z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 40) * (1f / (1 << 24));
        }
    }
}
