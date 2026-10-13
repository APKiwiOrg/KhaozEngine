using System;
using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public class ContactNavigationWaterTests
{
    internal static PhysicsNavBakeOptions PoolOptions => Options with
    { MinX = -3, MinZ = -3, MaxX = 3, MaxZ = 3, CellSize = 0.5f, MaxCells = 144, MaxLayerCells = 576 };

    [Fact]
    public void WadeOnlyDeepMiddleIsUnwalkable()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, PoolHeight, MovementWaterField.Level("world", 0, -4));
        using var capture = PhysicsNavBake.Capture(context, PoolOptions, _ => 0);
        MoveTuning tuning = Tuning with { Water = new(WaterTraversalMode.WadeOnly) };
        GroundNavigation nav = capture.BuildProfile(tuning, default);
        Assert.Null(Resting(nav, new(0.25f, -2, 0.25f)));
        NavPath route = nav.Planner.FindPath(new Vector3(-2.25f, 0, 0.25f), new Vector3(2.25f, 0, 0.25f),
            tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, route.Status);
        Assert.Contains(route.Waypoints, point => MathF.Abs(point.Position.Y) > 1.5f);
    }

    [Theory]
    [InlineData(0.04f, true)]
    [InlineData(0f, false)]
    public void SubmergedLipJudgedAtAnchor(float lip, bool admitted)
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f, Water = new(WaterTraversalMode.WadeOnly) };
        var water = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -1,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -1, -1), water: water,
            install: (world, builder) =>
            {
                Box(world, builder, "floor", new(-8, -1.2f, -8), new(8, -1, 8));
                if (lip > 0) Box(world, builder, "lip", new(0, -1, -2), new(2, -1 + lip, 2));
            });
        using var context = Context(scene, water);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        float? resting = Resting(capture.BuildProfile(tuning, default), new(-0.075f, -1, 0.125f));
        if (admitted) Assert.Equal(-0.96f, MathF.Round(resting!.Value, 6));
        else Assert.Null(resting);
    }

    [Theory]
    [InlineData(WaterTraversalMode.SurfaceSwimmer, 0.6499f, true)]
    [InlineData(WaterTraversalMode.SurfaceSwimmer, 0.65f, false)]
    [InlineData(WaterTraversalMode.SurfaceSwimmer, 0.6501f, false)]
    [InlineData(WaterTraversalMode.SurfaceSwimmer, 0.6f, true)]
    [InlineData(WaterTraversalMode.WadeOnly, 0.6499f, true)]
    [InlineData(WaterTraversalMode.WadeOnly, 0.65f, false)]
    [InlineData(WaterTraversalMode.WadeOnly, 0.6501f, false)]
    [InlineData(WaterTraversalMode.WadeOnly, 0.6f, true)]
    public void EnterFractionRows(WaterTraversalMode mode, float depth, bool admitted)
    {
        MoveTuning tuning = Tuning with { CapsuleHalfHeight = 0.5f, Water = new(mode) };
        var field = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -depth,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -depth, -depth), water: field,
            install: (world, builder) => Box(world, builder, "floor", new(-8, -depth - 0.2f, -8), new(8, -depth, 8)));
        using var context = Context(scene, field);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        Assert.Equal(admitted, Resting(capture.BuildProfile(tuning, default), new(-0.075f, -depth, 0.125f)).HasValue);
    }

    [Fact]
    public void DryOnlyHasNoNodeInWater()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, PoolHeight, MovementWaterField.Level("world", 0, -4));
        using var capture = PhysicsNavBake.Capture(context, PoolOptions, _ => 0);
        Assert.Null(Resting(capture.BuildProfile(Tuning, default), new(0.25f, -2, 0.25f)));

        var water = MovementWaterField.Level("world", 0, -4);
        using var scene = new OrdinaryContactScene(Tuning, (x, _) => x < 0 ? -0.5f : 0.125f,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -0.5f, 0.125f),
            water: water, install: (physics, builder) =>
            {
                Box(physics, builder, "shallow", new(-8, -0.75f, -8), new(0, -0.5f, 8));
                Box(physics, builder, "dry", new(0, -0.125f, -8), new(8, 0.125f, 8));
            });
        using var shallowContext = Context(scene, water);
        using var shallowCapture = PhysicsNavBake.Capture(shallowContext, Options, _ => 0);
        foreach (var mode in new[] { WaterTraversalMode.SurfaceSwimmer, WaterTraversalMode.WadeOnly, WaterTraversalMode.DryOnly })
        {
            GroundNavigation nav = shallowCapture.BuildProfile(Tuning with { Water = new(mode) }, default);
            Assert.Equal(mode != WaterTraversalMode.DryOnly, Resting(nav, new(-0.575f, -0.5f, 0.125f)).HasValue);
            Assert.Equal(0.125f, Resting(nav, new(0.675f, 0.125f, 0.125f)));
        }
    }

    [Fact]
    public void UnknownWaterRefuses()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = Context(world, water: new UnknownField());
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigation nav = capture.BuildProfile(Tuning, default);
        Assert.Null(Resting(nav, new(-0.075f, 0, 0.125f)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPoolBakesUnchanged(bool aquatic)
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = Context(world, PoolHeight, MovementWaterField.Level("world", 0, -4),
            SwimTraversalProbeTests.PoolMedium);
        using var capture = PhysicsNavBake.Capture(context, PoolOptions with { SampleWater = true }, _ => 0);
        var legacy = new NavBakeProfile("a-legacy", Tuning with { Controller = MovementControllerKind.Legacy, Water = default }, default)
        { Aquatic = aquatic };
        var contact = new NavBakeProfile("z-contact", Tuning with { Water = new(WaterTraversalMode.WadeOnly) }, default);
        var sources = new NavBakeSources().AddHashOf("pool", "fixture"u8);
        byte[] before = Payload(GroundNavigationBake.Create(capture, sources, [legacy]));
        byte[] combined = Payload(GroundNavigationBake.Create(capture, sources, [legacy, contact]));
        Assert.Equal(before, combined.AsSpan(0, before.Length).ToArray());
        Assert.Equal(before, Payload(GroundNavigationBake.Create(capture, sources, [legacy])));
        using var legacyContext = SwimTraversalProbeTests.PoolContext(world);
        using var legacyCapture = PhysicsNavBake.Capture(legacyContext, capture.Options, _ => 0);
        Assert.Equal(before, Payload(GroundNavigationBake.Create(legacyCapture, sources, [legacy])));
    }

    internal static float PoolHeight(float x, float z) => SwimTraversalProbeTests.InPit(x, z) ? -2 : 0;

    static byte[] Payload(GroundNavigationBake bake)
    {
        using var output = new MemoryStream();
        bake.WriteTo(output);
        byte[] bytes = output.ToArray();
        int start = GroundNavigationBake.HeaderLength + BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        return bytes.AsSpan(start).ToArray();
    }

    sealed class UnknownField : IMovementWaterField
    {
        readonly IMovementWaterField _inner = MovementWaterField.Level("world", 0, -4);
        public string SemanticIdentity => "unknown-water";
        public long Generation => 0;
        public MovementWaterExtent Extent => _inner.Extent;
        public MovementWaterPoint SampleCentreWater(in MovementBodyQuery body, Vector3 physicsOrigin) =>
            new(MovementAvailability.Unresolved, body.CurrentSpace, null, false, 1, null);
        public MovementCoverageResult TraceWater(in MovementMediumSweepQuery query, Vector3 physicsOrigin,
            in MovementQueryIdentity identity, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
            _inner.TraceWater(query, physicsOrigin, identity, spans, contacts);
    }
}
