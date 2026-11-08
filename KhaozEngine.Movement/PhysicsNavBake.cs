using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Captures bounded immutable absolute columns from caller-owned static physics.
/// Capture and later profile construction are sequential. Keep statics and origin unchanged until
/// profile construction finishes, then dispose the builder to release its provider references.</summary>
public sealed partial class PhysicsNavBake : IDisposable
{
    private GroundMoveContext? _context;
    private PhysicsNavCaptureBasis? _basis;
    private NavAreaClassifier? _classifier;

    private PhysicsNavBake(GroundMoveContext context, PhysicsNavBakeOptions options,
        Vector3 origin, PhysicsNavColumns columns, PhysicsNavCaptureBasis? basis, NavAreaClassifier classifier)
    {
        _context = context;
        Options = options;
        Origin = origin;
        Columns = columns;
        _basis = basis;
        _classifier = classifier;
    }

    internal PhysicsNavBakeOptions Options { get; }
    internal Vector3 Origin { get; }
    internal PhysicsNavColumns Columns { get; }
    internal PhysicsNavCaptureBasis? Basis
    {
        get
        {
            if (_context is null) throw new ObjectDisposedException(nameof(PhysicsNavBake));
            return _basis;
        }
    }
    internal NavAreaClassifier Classifier => _classifier ?? throw new ObjectDisposedException(nameof(PhysicsNavBake));
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
    /// <exception cref="InvalidOperationException">The physics basis changes during capture, or water sampling
    /// needs a medium delegate for a contact water field, or the context has live runtime statics.</exception>
    public static PhysicsNavBake Capture(GroundMoveContext context, PhysicsNavBakeOptions options, NavAreaClassifier classify)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(classify);
        RequireAuthoredStatics(context);
        IPhysicsWorld world = context.Physics ?? throw new ArgumentException("Capture requires a physics world.", nameof(context));
        PhysicsNavCaptureBasis? basis = PhysicsNavCaptureBasis.Capture(world);
        RequireAuthoredStatics(context);
        Vector3 origin = basis?.Origin ?? world.Origin;
        if (!float.IsFinite(origin.X) || !float.IsFinite(origin.Y) || !float.IsFinite(origin.Z))
            throw new NavigationBuildException(NavigationBuildRefusal.PhysicsOriginNonfinite,
                "The physics origin must be finite.")
            { ActualOrigin = origin };
        (int width, int height, int cells, int maxSamples) = options.Validate(origin);
        Func<float, float, float, MovementMedium>? medium = !options.SampleWater ? null
            : context.CaptureMedium ?? throw new ArgumentException("Water sampling requires a medium.", nameof(context));
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
        var captureHooks = ContactTerrainCaptureHooks.Current;
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
                    throw new NavigationBuildException(NavigationBuildRefusal.ColumnSurfaceCapExceeded,
                        $"Physics column at ({absoluteX}, {absoluteZ}) exceeds the surface cap.")
                    { AbsoluteX = absoluteX, AbsoluteZ = absoluteZ, MaximumCount = options.MaxSurfacesPerColumn, ActualCount = count };
                float previous = float.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    ColumnSurface surface = buffer[i];
                    float absoluteY = surface.Height + origin.Y;
                    ValidateCapturedSurface(x, z, i, absoluteY, previous, surface.Headroom);
                    uint areas = classify(new Vector3(absoluteX, absoluteY, absoluteZ));
                    basis?.AssertCurrent();
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
                basis?.AssertCurrent();
                EnsureOrigin(world, origin);
                water[wet++] = new PhysicsNavWater(cell, sample.WaterSurfaceY, waterAreas);
            }
            captureHooks?.Progress?.Invoke(ContactTerrainCaptureStage.RawColumns, z + 1, height);
        }
        starts[cells] = stored;
        basis?.AssertCurrent();
        EnsureOrigin(world, origin);
        var columns = new PhysicsNavColumns(options, width, height, starts, surfaces.AsSpan(0, stored), water.AsSpan(0, wet));
        var capture = new PhysicsNavBake(context, options, origin, columns, basis, classify);
        try
        {
            if (options.CaptureTerrainDescentEvidence) capture.DescentEvidence = PhysicsNavDescentCapture.Capture(capture);
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    /// <summary>Releases retained builder providers without disposing or changing caller-owned physics.</summary>
    public void Dispose()
    {
        _context = null;
        _basis = null;
        _classifier = null;
    }

    private static void EnsureOrigin(IPhysicsWorld world, Vector3 origin)
    {
        Vector3 actual = world.Origin;
        if (actual != origin)
            throw new NavigationBuildException(NavigationBuildRefusal.CaptureOriginChanged,
                "The physics origin changed after capture began.")
            { ExpectedOrigin = origin, ActualOrigin = actual };
    }

    private static void RequireAuthoredStatics(GroundMoveContext context)
    {
        if (context.MovementRegistration?.HasRuntimeStatics == true)
            throw new NavigationBuildException(NavigationBuildRefusal.RuntimeStaticsPresent,
                "Authored navigation cannot capture or build profiles with live runtime statics. " +
                "Use an authored-only installation or bake before admitting runtime changes.")
            { SourceRole = NavigationSourceRole.MovementRegistration };
    }
}
