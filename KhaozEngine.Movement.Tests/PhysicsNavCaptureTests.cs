using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class PhysicsNavCaptureTests
{
    private static readonly Vector3 RebasedOrigin = new(100f, 20f, -80f);
    private static readonly PhysicsNavBakeOptions Options = new(
        102f, -77f, 104f, -75f, 1f, 8f, 10f, 0.8f, 16, 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FloorAndDeckKeepAbsoluteHeightsHeadroomAndPerSurfaceTags(bool rebase)
    {
        using var world = BridgeWorld(rebase ? RebasedOrigin : Vector3.Zero);
        var samples = new List<Vector3>();
        using var bake = PhysicsNavBake.Capture(PhysicsOnly(world), Options, feet =>
        {
            samples.Add(feet);
            return feet.Y < 2f ? 0x01u : 0x02u;
        });

        Assert.Equal(2, bake.Columns.Width);
        Assert.Equal(2, bake.Columns.Height);
        Assert.Equal(8, bake.Columns.SurfaceCount);
        var column = bake.Columns.GetColumn(0, 0);
        Assert.Equal(2, column.Length);
        Assert.Equal(1f, column[0].Height, 5);
        Assert.InRange(column[0].Headroom, 2.49f, 2.52f);
        Assert.Equal(0x01u, column[0].Areas);
        Assert.Equal(4f, column[1].Height, 5);
        Assert.True(float.IsPositiveInfinity(column[1].Headroom));
        Assert.Equal(0x02u, column[1].Areas);
        AssertSamples(new Vector3[]
        {
            new(102.5f, 1f, -76.5f), new(102.5f, 4f, -76.5f),
            new(103.5f, 1f, -76.5f), new(103.5f, 4f, -76.5f),
            new(102.5f, 1f, -75.5f), new(102.5f, 4f, -75.5f),
            new(103.5f, 1f, -75.5f), new(103.5f, 4f, -75.5f),
        }, samples);
    }

    [Fact]
    public void MissingOuterColumnStaysBlocked()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(0.4f, 0.1f, 0.4f)), Pose.At(new Vector3(-1f, -0.1f, 0f)));
        PhysicsNavBakeOptions options = Options with
        {
            MinX = -1.5f,
            MinZ = -0.5f,
            MaxX = 1.5f,
            MaxZ = 0.5f,
            ProbeHeight = 2f,
            ProbeRange = 3f,
        };
        using var bake = PhysicsNavBake.Capture(PhysicsOnly(world), options, _ => 0x01u);

        Assert.Equal(1, bake.Columns.GetColumn(0, 0).Length);
        Assert.True(bake.Columns.GetColumn(1, 0).IsEmpty);
        Assert.True(bake.Columns.GetColumn(2, 0).IsEmpty);
        Span<NavSurfaceSample> buffer = stackalloc NavSurfaceSample[4];
        Assert.Equal(0, bake.Columns.SampleColumn(1f, 0f, buffer));
        Assert.Equal(0, bake.Columns.SampleColumn(options.MaxX, 0f, buffer));
    }

    [Fact]
    public void ExactMeshOuterEdgeKeepsThePhysicsProbeResult()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(new TriangleMeshShape(
            [new(0f, 0f, 0f), new(0f, 0f, 1f), new(1f, 0f, 0f)], [0, 1, 2]), Pose.At(Vector3.Zero));
        PhysicsNavBakeOptions options = Options with
        {
            MinX = 0.5f,
            MinZ = -0.5f,
            MaxX = 1.5f,
            MaxZ = 0.5f,
            ProbeHeight = 2f,
            ProbeRange = 3f,
        };
        var probe = new PhysicsColumnProbe(world) { ProbeHeight = 2f, ProbeRange = 3f, MaxSlopeRadians = 0.8f };
        Span<ColumnSurface> raw = stackalloc ColumnSurface[5];
        int count = probe.Sample(1f, 0f, raw);

        using var bake = PhysicsNavBake.Capture(PhysicsOnly(world), options, _ => 0x01u);

        Assert.Equal(count, bake.Columns.GetColumn(0, 0).Length);
        if (count != 0) Assert.Equal(raw[0].Height, bake.Columns.GetColumn(0, 0)[0].Height, 5);
    }

    [Fact]
    public void NegativeNonintegralBoundsLeavePaddedCentersEmpty()
    {
        using var world = FlatWorld();
        var samples = new List<Vector3>();
        PhysicsNavBakeOptions options = Options with
        {
            MinX = -2f,
            MinZ = -3f,
            MaxX = 0.2f,
            MaxZ = -0.8f,
            ProbeHeight = 2f,
            ProbeRange = 3f,
        };
        using var bake = PhysicsNavBake.Capture(PhysicsOnly(world), options, feet =>
        {
            samples.Add(feet);
            return 0x01u;
        });

        Assert.Equal(3, bake.Columns.Width);
        Assert.Equal(3, bake.Columns.Height);
        Assert.Equal(4, bake.Columns.SurfaceCount);
        AssertSamples(new Vector3[]
        {
            new(-1.5f, 0f, -2.5f), new(-0.5f, 0f, -2.5f),
            new(-1.5f, 0f, -1.5f), new(-0.5f, 0f, -1.5f),
        }, samples);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(bake.Columns.GetColumn(2, i).IsEmpty);
            Assert.True(bake.Columns.GetColumn(i, 2).IsEmpty);
        }
    }

    [Fact]
    public void FractionalDimensionsMatchGroundedBakeFloatCeiling()
    {
        using var world = FlatWorld();
        PhysicsNavBakeOptions options = Options with
        {
            MinX = 0f,
            MinZ = 0f,
            MaxX = 0.3f,
            MaxZ = 0.3f,
            CellSize = 0.1f,
            ProbeHeight = 2f,
            ProbeRange = 3f,
            MaxCells = 9,
            MaxLayerCells = 9,
        };
        using var bake = PhysicsNavBake.Capture(PhysicsOnly(world), options, _ => 0x01u);

        Assert.Equal(3, bake.Columns.Width);
        Assert.Equal(3, bake.Columns.Height);
        Assert.Equal(9, bake.Columns.SurfaceCount);
    }

    [Fact]
    public void SurfaceCapRejectsTruncationAndAcceptsTheExactCap()
    {
        using var world = BridgeWorld(Vector3.Zero);
        var context = PhysicsOnly(world);

        Assert.Throws<InvalidOperationException>(() => PhysicsNavBake.Capture(context,
            Options with { MaxSurfacesPerColumn = 1 }, _ => 0x01u));
        using var bake = PhysicsNavBake.Capture(context, Options with { MaxSurfacesPerColumn = 2 }, _ => 0x01u);

        Assert.Equal(2, bake.Columns.GetColumn(0, 0).Length);
    }

    [Fact]
    public void ClassifierMutationCannotRewriteCapturedTags()
    {
        using var world = BridgeWorld(Vector3.Zero);
        uint area = 0x01u;
        bool sampledAfterCapture = false;
        using var bake = PhysicsNavBake.Capture(PhysicsOnly(world), Options, _ =>
            sampledAfterCapture ? throw new InvalidOperationException() : area);
        area = 0x02u;
        sampledAfterCapture = true;

        Assert.Equal(0x01u, bake.Columns.GetColumn(0, 0)[0].Areas);
        Assert.Equal(0x01u, bake.Columns.GetColumn(0, 0)[1].Areas);
        Span<NavSurfaceSample> buffer = stackalloc NavSurfaceSample[4];
        Assert.Equal(2, bake.Columns.SampleColumn(102.5f, -76.5f, buffer));
    }

    [Fact]
    public void OriginChangeInsideClassifierRejectsCapture()
    {
        using var world = BridgeWorld(RebasedOrigin);

        Assert.Throws<InvalidOperationException>(() => PhysicsNavBake.Capture(PhysicsOnly(world), Options, _ =>
        {
            world.Rebase(new Vector3(96f, 12f, -72f));
            return 0x01u;
        }));
    }

    [Fact]
    public void BuilderContextRejectsOriginChangesBetweenCaptureAndProfiles()
    {
        using var world = BridgeWorld(Vector3.Zero);
        using var bake = PhysicsNavBake.Capture(PhysicsOnly(world), Options, _ => 0x01u);
        world.Rebase(RebasedOrigin);

        Assert.Throws<InvalidOperationException>(() => { _ = bake.Context; });
        Assert.Equal(1f, bake.Columns.GetColumn(0, 0)[0].Height, 5);
    }

    [Fact]
    public void DisposeBlocksBuilderAccessWithoutRemovingCallerStatics()
    {
        PhysicsNavBake bake;
        using (var world = BridgeWorld(Vector3.Zero))
        {
            bake = PhysicsNavBake.Capture(PhysicsOnly(world), Options, _ => 0x01u);
            bake.Dispose();
            bake.Dispose();

            Assert.Throws<ObjectDisposedException>(() => { _ = bake.Context; });
            Assert.True(world.Raycast(new Vector3(102.5f, 8f, -76.5f), -Vector3.UnitY, 10f,
                out RayHit hit, new QueryFilter(QueryMobility.Statics)));
            Assert.Equal(4f, hit.Point.Y, 5);
        }
        Assert.Equal(2, bake.Columns.GetColumn(0, 0).Length);
        Span<NavSurfaceSample> buffer = stackalloc NavSurfaceSample[4];
        Assert.Equal(2, bake.Columns.SampleColumn(102.5f, -76.5f, buffer));
    }

    [Fact]
    public void ColumnStoreOwnsItsInputsAndCannotBeRewrittenThroughSampleOutput()
    {
        int[] starts = [0, 1];
        PhysicsNavSurface[] data = [new(4f, float.PositiveInfinity, 0x01u)];
        var columns = new PhysicsNavColumns(Options, 1, 1, starts, data, []);
        starts[1] = 0;
        data[0] = new PhysicsNavSurface(9f, 0f, 0x02u);
        Span<NavSurfaceSample> buffer = stackalloc NavSurfaceSample[1];
        Assert.Equal(1, columns.SampleColumn(102.5f, -76.5f, buffer));
        buffer[0] = new NavSurfaceSample(false, 9f, 0f);

        Assert.Equal(1, columns.GetColumn(0, 0).Length);
        Assert.Equal(4f, columns.GetColumn(0, 0)[0].Height);
        Assert.Equal(0x01u, columns.GetColumn(0, 0)[0].Areas);
    }

    [Fact]
    public void CaptureRejectsMissingInputsAndPhysics()
    {
        using var world = FlatWorld();
        var context = PhysicsOnly(world);

        Assert.Throws<ArgumentNullException>(() => PhysicsNavBake.Capture(null!, Options, _ => 0u));
        Assert.Throws<ArgumentNullException>(() => PhysicsNavBake.Capture(context, null!, _ => 0u));
        Assert.Throws<ArgumentNullException>(() => PhysicsNavBake.Capture(context, Options, null!));
        Assert.Throws<ArgumentException>(() => PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f), Options, _ => 0u));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void CaptureRejectsNonfiniteOriginBeforeQuerying(float coordinate)
    {
        using var world = new InvalidOriginWorld(new Vector3(coordinate, 0f, 0f));

        Assert.Throws<InvalidOperationException>(() => PhysicsNavBake.Capture(PhysicsOnly(world), Options, _ => 0u));
    }

    [Fact]
    public void CaptureRejectsNonfiniteLocalProbeCoordinates()
    {
        using var world = new BepuPhysicsWorld();
        world.Rebase(new Vector3(-float.MaxValue, 0f, 0f));
        PhysicsNavBakeOptions options = Options with
        {
            MinX = float.MaxValue / 2f,
            MaxX = float.MaxValue,
            CellSize = float.MaxValue / 2f,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => PhysicsNavBake.Capture(PhysicsOnly(world), options, _ => 0u));
    }

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void InvalidOptionsAreRejectedBeforeClassifyingOrAllocating(PhysicsNavBakeOptions options)
    {
        using var world = FlatWorld();

        Assert.Throws<ArgumentOutOfRangeException>(() => PhysicsNavBake.Capture(PhysicsOnly(world), options,
            _ => throw new InvalidOperationException("Invalid options reached classification.")));
    }

    public static IEnumerable<object[]> InvalidOptions()
    {
        PhysicsNavBakeOptions[] invalid =
        [
            Options with { MinX = float.NaN }, Options with { MinZ = float.NegativeInfinity },
            Options with { MaxX = float.PositiveInfinity }, Options with { MaxZ = float.NaN },
            Options with { MaxX = 102f }, Options with { MaxZ = -78f },
            Options with { CellSize = 0f }, Options with { CellSize = -1f }, Options with { CellSize = float.NaN },
            Options with { ProbeHeight = float.NaN }, Options with { ProbeHeight = float.PositiveInfinity },
            Options with { ProbeRange = 0f }, Options with { ProbeRange = float.PositiveInfinity },
            Options with { ProbeHeight = -float.MaxValue, ProbeRange = float.MaxValue },
            Options with { MaxSlopeRadians = -0.1f }, Options with { MaxSlopeRadians = float.NaN },
            Options with { MaxSlopeRadians = MathF.PI / 2f },
            Options with { MaxCells = 0 }, Options with { MaxCells = 3 },
            Options with { MaxLayerCells = 0 }, Options with { MaxLayerCells = 3 },
            Options with { MaxSurfacesPerColumn = 0 }, Options with { MaxSurfacesPerColumn = int.MaxValue },
            Options with { EdgeProbeSeconds = 0f }, Options with { EdgeProbeSeconds = float.NaN },
            Options with { EdgeProbeSeconds = float.MaxValue, MaxEdgeProbeSteps = 2 },
            Options with { MaxEdgeProbeSteps = 0 },
            Options with { MinX = -float.MaxValue, MaxX = float.MaxValue },
            Options with { CellSize = float.Epsilon },
            Options with { MinX = 0f, MinZ = 0f, MaxX = 50000f, MaxZ = 50000f, MaxCells = int.MaxValue, MaxLayerCells = int.MaxValue },
            Options with { MaxSurfacesPerColumn = int.MaxValue / 4 },
        ];
        foreach (PhysicsNavBakeOptions options in invalid) yield return [options];
    }

    [Theory]
    [InlineData(0x03u, 0x04u, 0x03u, true)]
    [InlineData(0x03u, 0x04u, 0x01u, false)]
    [InlineData(0x03u, 0x04u, 0x07u, false)]
    [InlineData(0u, 0x04u, 0u, true)]
    [InlineData(0u, 0u, 0x08u, true)]
    public void AreaFilterRequiresEveryRequiredBitAndNoExcludedBits(uint required, uint excluded, uint tags, bool accepted)
    {
        Assert.Equal(accepted, new NavAreaFilter(required, excluded).Allows(tags));
    }

    [Fact]
    public void AreaFilterRejectsOverlappingMasks()
    {
        Assert.Throws<ArgumentException>(() => new NavAreaFilter(0x03u, 0x02u));
    }

    private static GroundMoveContext PhysicsOnly(IPhysicsWorld world) => new(
        (_, _) => throw new InvalidOperationException("Capture must not sample analytic ground."), physics: world);

    private static void AssertSamples(Vector3[] expected, List<Vector3> actual)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, actual[i].X);
            Assert.Equal(expected[i].Y, actual[i].Y, 5);
            Assert.Equal(expected[i].Z, actual[i].Z);
        }
    }

    private static BepuPhysicsWorld BridgeWorld(Vector3 origin)
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(4f, 0.1f, 4f)), Pose.At(new Vector3(103f, 0.9f, -76f)));
        world.AddStatic(new BoxShape(new Vector3(4f, 0.25f, 4f)), Pose.At(new Vector3(103f, 3.75f, -76f)));
        if (origin != Vector3.Zero) world.Rebase(origin);
        return world;
    }

    private static BepuPhysicsWorld FlatWorld()
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(8f, 0.1f, 8f)), Pose.At(new Vector3(0f, -0.1f, 0f)));
        return world;
    }

    // Bepu refuses nonfinite origins, so this seam fixture represents an invalid provider.
    // No physics operation is legal before capture rejects its origin.
    private sealed class InvalidOriginWorld(Vector3 origin) : IPhysicsWorld
    {
        public Vector3 Origin => origin;
        public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null) => throw new NotSupportedException();
        public void RemoveStatic(StaticHandle handle) => throw new NotSupportedException();
        public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null) => throw new NotSupportedException();
        public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
        public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();
        public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular) => throw new NotSupportedException();
        public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular) => throw new NotSupportedException();
        public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();
        public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
        public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
        public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
        public void Step(float dt) => throw new NotSupportedException();
        public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, out RayHit hit, QueryFilter filter = default) => throw new NotSupportedException();
        public bool SweepCapsule(CapsuleShape capsule, Pose pose, Vector3 direction, float maxDistance, out SweepHit hit, QueryFilter filter = default) => throw new NotSupportedException();
        public bool ComputePenetration(CapsuleShape capsule, Pose pose, out Vector3 mtv) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
