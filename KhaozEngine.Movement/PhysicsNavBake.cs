using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Captures bounded immutable absolute columns from caller-owned static physics.
/// Capture and later profile construction are sequential. Keep statics and origin unchanged until
/// profile construction finishes, then dispose the builder to release its context references.</summary>
public sealed partial class PhysicsNavBake : IDisposable
{
    private GroundMoveContext? _context;

    private PhysicsNavBake(GroundMoveContext context, PhysicsNavBakeOptions options,
        Vector3 origin, PhysicsNavColumns columns)
    {
        _context = context;
        Options = options;
        Origin = origin;
        Columns = columns;
    }

    internal PhysicsNavBakeOptions Options { get; }
    internal Vector3 Origin { get; }
    internal PhysicsNavColumns Columns { get; }
    internal GroundMoveContext Context
    {
        get
        {
            GroundMoveContext context = _context ?? throw new ObjectDisposedException(nameof(PhysicsNavBake));
            EnsureOrigin(context.Physics!, Origin);
            return context;
        }
    }

    /// <summary>Samples static physics at cell centers, assigning and freezing area bits per surface.
    /// Missing physics columns and padded centers beyond the half-open bounds remain empty.
    /// Classification receives absolute feet positions. No analytic ground provider is sampled.
    /// With <see cref="PhysicsNavBakeOptions.SampleWater"/> the context's medium is sampled once per in-bounds column at
    /// its lowest surface, or at <c>ProbeHeight - ProbeRange</c> when the column is empty. An in-water sample with a
    /// finite surface above that height records one water entry, classified at the water surface point. Without the
    /// option the medium is never called.</summary>
    /// <exception cref="ArgumentException">The context has no physics, or the options sample water and the context has
    /// no medium.</exception>
    public static PhysicsNavBake Capture(GroundMoveContext context, PhysicsNavBakeOptions options, NavAreaClassifier classify)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(classify);
        IPhysicsWorld world = context.Physics ?? throw new ArgumentException("Capture requires a physics world.", nameof(context));
        Vector3 origin = world.Origin;
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            throw new InvalidOperationException("The physics origin must be finite.");
        (int width, int height, int cells, int maxSamples) = options.Validate(origin);
        Func<float, float, float, MovementMedium>? medium = !options.SampleWater ? null
            : context.Medium ?? throw new ArgumentException("Water sampling requires a medium.", nameof(context));
        var starts = new int[cells + 1];
        var surfaces = new PhysicsNavSurface[maxSamples];
        PhysicsNavWater[] water = medium is null ? [] : new PhysicsNavWater[cells];
        float probeFloor = options.ProbeHeight - options.ProbeRange;
        int wet = 0;
        var buffer = new ColumnSurface[options.MaxSurfacesPerColumn + 1];
        var probe = new PhysicsColumnProbe(world)
        {
            ProbeHeight = options.ProbeHeight - origin.Y,
            ProbeRange = options.ProbeRange,
            MaxSlopeRadians = options.MaxSlopeRadians,
            GroundMobility = QueryMobility.Statics,
        };
        int stored = 0;
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int cell = z * width + x;
                starts[cell] = stored;
                float absoluteX = options.MinX + (x + 0.5f) * options.CellSize;
                float absoluteZ = options.MinZ + (z + 0.5f) * options.CellSize;
                if (!float.IsFinite(absoluteX) || !float.IsFinite(absoluteZ))
                    throw new ArgumentOutOfRangeException(nameof(options), "Cell centers must be finite.");
                if (absoluteX >= options.MaxX || absoluteZ >= options.MaxZ) continue;
                EnsureOrigin(world, origin);
                int count = probe.Sample(absoluteX - origin.X, absoluteZ - origin.Z, buffer);
                EnsureOrigin(world, origin);
                if (count > options.MaxSurfacesPerColumn)
                    throw new InvalidOperationException($"Physics column at ({absoluteX}, {absoluteZ}) exceeds the surface cap.");
                float previous = float.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    ColumnSurface surface = buffer[i];
                    float absoluteY = surface.Height + origin.Y;
                    if (!float.IsFinite(absoluteY) || absoluteY <= previous ||
                        float.IsNaN(surface.Headroom) || surface.Headroom < 0f)
                        throw new InvalidOperationException("Physics surfaces must have finite ascending heights and nonnegative headroom.");
                    uint areas = classify(new Vector3(absoluteX, absoluteY, absoluteZ));
                    EnsureOrigin(world, origin);
                    surfaces[stored++] = new PhysicsNavSurface(absoluteY, surface.Headroom, areas);
                    previous = absoluteY;
                }
                if (medium is null) continue;
                float feetY = count > 0 ? surfaces[starts[cell]].Height : probeFloor;
                MovementMedium sample = medium(absoluteX, absoluteZ, feetY);
                EnsureOrigin(world, origin);
                if (!sample.InWater || !float.IsFinite(sample.WaterSurfaceY) || sample.WaterSurfaceY <= feetY) continue;
                uint waterAreas = classify(new Vector3(absoluteX, sample.WaterSurfaceY, absoluteZ));
                EnsureOrigin(world, origin);
                water[wet++] = new PhysicsNavWater(cell, sample.WaterSurfaceY, waterAreas);
            }
        }
        starts[cells] = stored;
        EnsureOrigin(world, origin);
        var columns = new PhysicsNavColumns(options, width, height, starts, surfaces.AsSpan(0, stored), water.AsSpan(0, wet));
        return new PhysicsNavBake(context, options, origin, columns);
    }

    /// <summary>Releases retained builder providers without disposing or changing caller-owned physics.</summary>
    public void Dispose() => _context = null;

    private static void EnsureOrigin(IPhysicsWorld world, Vector3 origin)
    {
        if (world.Origin != origin)
            throw new InvalidOperationException("The physics origin changed after capture began.");
    }
}
