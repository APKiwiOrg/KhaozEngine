using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactNavigationFixture;

namespace KhaozEngine.Tests.Movement;

public sealed class ContactNavigationProofReuseProtectionTests
{
    [Theory]
    [InlineData(ContactNavigationReuseMode.Off)]
    [InlineData(ContactNavigationReuseMode.Footprints)]
    [InlineData(ContactNavigationReuseMode.Combined)]
    public void FootprintReuseKeepsPoolAndDirectionalStairRefusals(ContactNavigationReuseMode mode)
    {
        GroundNavigation pool = Build("wade-only-pool"), stairs = Build("stair-accepted-refused"), bands = Build("multi-layer-row-bands");
        Assert.Equal(8, RejectedNeighbours(pool));
        Assert.Equal(4, AsymmetricPairs(stairs));
        Assert.Equal(28, AsymmetricPairs(bands));

        GroundNavigation Build(string fixture)
        {
            var reference = ContactNavigationReuseFixture.Build(fixture, ContactNavigationReuseMode.Off, 1);
            var candidate = ContactNavigationReuseFixture.Build(fixture, mode, 3);
            Assert.Equal(GroundNavigationBakeRoundTripTests.Write(reference.Bake), GroundNavigationBakeRoundTripTests.Write(candidate.Bake));
            return candidate.Bake.GetProfile("player");
        }
    }

    static int RejectedNeighbours(GroundNavigation nav)
    {
        int count = 0;
        for (int layer = 0; layer < nav.Space.Layers.Count; layer++)
        {
            NavGrid grid = nav.Space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                    for (int direction = 0; direction < 8; direction++)
                    {
                        int nx = x + ContactNavigationReuseFixture.Directions[direction].X, nz = z + ContactNavigationReuseFixture.Directions[direction].Z;
                        if (nav.Graph.IsNodePassable(layer, x, z) && nav.Graph.IsNodePassable(layer, nx, nz) &&
                            grid.RestingHeightAt(x, z) == grid.RestingHeightAt(nx, nz) &&
                            !nav.Graph.CanTraverse(layer, x, z, layer, nx, nz)) count++;
                    }
        }
        return count;
    }

    static int AsymmetricPairs(GroundNavigation nav)
    {
        int count = nav.Space.Links.Count(link =>
            nav.Graph.CanTraverse(link.FromLayer, link.FromX, link.FromZ, link.ToLayer, link.ToX, link.ToZ) &&
            !nav.Graph.CanTraverse(link.ToLayer, link.ToX, link.ToZ, link.FromLayer, link.FromX, link.FromZ));
        for (int layer = 0; layer < nav.Space.Layers.Count; layer++)
        {
            NavGrid grid = nav.Space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                    for (int direction = 0; direction < 8; direction++)
                    {
                        int nx = x + ContactNavigationReuseFixture.Directions[direction].X;
                        int nz = z + ContactNavigationReuseFixture.Directions[direction].Z;
                        if (nav.Graph.CanTraverse(layer, x, z, layer, nx, nz) &&
                            !nav.Graph.CanTraverse(layer, nx, nz, layer, x, z)) count++;
                    }
        }
        return count;
    }

    [Theory]
    [InlineData("forbidden-corner")]
    [InlineData("thin-wall")]
    [InlineData("lifted-roof")]
    [InlineData("lifted-roof-clear")]
    [InlineData("next-raw-headroom")]
    [InlineData("unsupported-hole")]
    [InlineData("lower-deck")]
    [InlineData("changing-disc")]
    public void ProtectionCasesKeepSerialVerdict(string scenario)
    {
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.3f };
        using var scene = new OrdinaryContactScene(tuning,
            height: scenario == "unsupported-hole" ? (x, _) => x > -0.4f && x < 0.4f ? -2 : 0 : null,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -2, 0), install: (world, builder) =>
        {
            if (scenario == "unsupported-hole")
            {
                Box(world, builder, "west", new(-8, -0.2f, -8), new(-0.4f, 0, 8));
                Box(world, builder, "east", new(0.4f, -0.2f, -8), new(8, 0, 8));
            }
            else Box(world, builder, "floor", new(-8, -0.2f, -8), new(8, 0, 8));
            if (scenario == "thin-wall") Box(world, builder, "wall", new(-0.005f, 0, -2), new(0.005f, 2, 2));
            if (scenario is "lifted-roof" or "lifted-roof-clear")
            {
                Box(world, builder, "lip", new(-0.1f, 0, -2), new(0.1f, 0.1875f, 2));
                if (scenario == "lifted-roof")
                    Box(world, builder, "roof", new(-0.05f, 1.6f, -2), new(0.05f, 1.7f, 2));
            }
            if (scenario == "next-raw-headroom")
            {
                Box(world, builder, "lip", new(0, 0, -2), new(2, 0.04f, 2));
                Box(world, builder, "roof", new(-1, 1.52f, -2), new(1, 1.62f, 2));
            }
            if (scenario == "lower-deck") Box(world, builder, "deck", new(-2, 2, -2), new(2, 2.1f, 2));
            if (scenario == "changing-disc") Box(world, builder, "step", new(0, 0, -2), new(2, 0.42f, 2));
        });
        using var context = Context(scene);
        var options = Options with { MinX = -2, MaxX = 2, MinZ = -2, MaxZ = 2, MaxCells = 256, MaxLayerCells = 1024 };
        using var capture = PhysicsNavBake.Capture(context, options,
            sample => scenario == "forbidden-corner" && sample.X >= 0 && sample.Z >= 0 ? 1u : 0u);
        var areas = new NavAreaFilter(0, 1);
        var footprint = new NavAreaFootprint(capture.Columns, options, tuning, areas);
        using var source = context.CreateNavigationBinding(tuning);
        Vector3 from = scenario == "forbidden-corner" ? new(-0.125f, 0, -0.125f) : new(-0.5f, 0, 0.125f);
        Vector3 to = scenario == "forbidden-corner" ? from : new(0.5f, 0, 0.125f);
        bool expected;
        using (var reference = new ContactNavigationProof(capture, context, source, footprint, new(false, false, false)))
        {
            if (scenario is "lifted-roof" or "lifted-roof-clear")
            {
                Assert.True(footprint.Accepts(from));
                Assert.True(footprint.Accepts(to));
                Assert.True(reference.TryEdge(from, from));
                Assert.True(reference.TryEdge(to, to));
            }
            expected = reference.TryEdge(from, to);
        }
        Assert.Equal(scenario is "lower-deck" or "lifted-roof-clear", expected);
        foreach (ContactNavigationReuseMode mode in Enum.GetValues<ContactNavigationReuseMode>())
        {
            using var proof = new ContactNavigationProof(capture, context, source, footprint,
                new(mode != ContactNavigationReuseMode.Off, mode == ContactNavigationReuseMode.Combined, false));
            using (proof.BeginSource(ContactNavigationReuseFixture.Source()))
            {
                Assert.Equal(expected, proof.TryEdge(from, to));
                Assert.Equal(expected, proof.TryEdge(from, to));
            }
            ContactNavigationReuseFixture.Released();
        }
    }

    public static IEnumerable<object[]> ArithmeticCases =>
        from origin in new[] { Vector3.Zero, new Vector3(8, 2, -4), new Vector3(-1024, -2, 1024), new Vector3(1024, 2, -1024) }
        from budget in new[] { 1, 2, 160 }
        select new object[] { origin.X, origin.Y, origin.Z, budget };

    [Theory]
    [MemberData(nameof(ArithmeticCases))]
    public void ArrivalReuseKeepsEuclideanEqualityAndAdjacentFloats(float originX, float originY, float originZ, int budget)
    {
        Vector3 origin = new(originX, originY, originZ);
        using var scene = new OrdinaryContactScene(Tuning, origin: origin,
            install: (world, builder) => Box(world, builder, "floor", new(-8, -0.25f, -8), new(8, 0, 8)));
        using var context = Context(scene);
        using var capture = PhysicsNavBake.Capture(context, Options with { MaxEdgeProbeSteps = budget }, _ => 0);
        using var source = context.CreateNavigationBinding(Tuning);
        var footprint = new NavAreaFootprint(capture.Columns, capture.Options, Tuning, default);
        using var reference = new ContactNavigationProof(capture, context, source, footprint, new(false, false, false));
        using var reused = new ContactNavigationProof(capture, context, source, footprint, new(true, true, false));
        Vector3 from = new(-0.0f, 0, float.Epsilon);
        MovementStepResult initial = Hold(from, false);
        bool referenceInitial = reference.TryEdge(from, from);
        Assert.Equal(initial.Outcome == MovementStepOutcome.Advanced && initial.State.State.Grounded &&
            GroundTraversalProbe.Near(Feet(initial), from) && footprint.Accepts(Feet(initial)), referenceInitial);
        if (originX == 0) Assert.True(referenceInitial);
        using (reused.BeginSource(ContactNavigationReuseFixture.Source()))
        {
            Assert.Equal(referenceInitial, reused.TryEdge(from, from));
            foreach (Vector3 target in new[] { from, new(MathF.BitDecrement(0.001f), 0, 0), new(0.001f, 0, 0),
            new(MathF.BitIncrement(0.001f), 0, 0), new(0.0008f, 0, 0.0008f), new(0.0008f, 0.0008f, 0),
            new(0.25f, 0, 0.25f), new(0.25f, 0, 0) })
            {
                bool expected = reference.TryEdge(from, target);
                Assert.Equal(expected, reused.TryEdge(from, target));
                if (budget == 1) Assert.Equal(initial.Outcome == MovementStepOutcome.Advanced && initial.State.State.Grounded &&
                    GroundTraversalProbe.Near(Feet(initial), from) && GroundTraversalProbe.Near(Feet(initial), target), expected);
            }
        }
        Assert.True(GroundTraversalProbe.Near(Vector3.Zero, new(0.001f, 0, 0)));
        Assert.True(GroundTraversalProbe.Near(Vector3.Zero, new(MathF.BitDecrement(0.001f), 0, 0)));
        Assert.False(GroundTraversalProbe.Near(Vector3.Zero, new(MathF.BitIncrement(0.001f), 0, 0)));
        Assert.False(GroundTraversalProbe.Near(Vector3.Zero, new(0.0008f, 0, 0.0008f)));
        Assert.False(GroundTraversalProbe.Near(Vector3.Zero, new(0.0008f, 0.0008f, 0)));
        foreach (float feet in new[] { -0.0f, float.Epsilon, -float.Epsilon, 0.025f, 0.1875f })
        {
            Vector3 raw = new(0, feet, 0);
            MovementStepResult referenceHold = Hold(raw, false), reusedHold = Hold(raw, true);
            Assert.Equal(referenceHold.Outcome, reusedHold.Outcome);
            Assert.Equal(referenceHold.State.State.Grounded, reusedHold.State.State.Grounded);
            Assert.Equal(Bits(Feet(referenceHold)), Bits(Feet(reusedHold)));
        }

        MovementStepResult Hold(Vector3 raw, bool reuse)
        {
            using var binding = source.CreateProofBinding();
            var frame = context.NavigationFrame;
            var scope = binding.CreateOrdinaryScope(frame);
            var state = new FramedMovementState(new MoveState
            { Position = raw - frame.PhysicsOrigin + Vector3.UnitY * Tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1 }, frame, null);
            IPhysicsSupportFootprintReuseSession? session = null;
            try
            {
                Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out var first));
                MovementStepResult result;
                using (first)
                {
                    if (reuse)
                    {
                        Assert.True(first!.TryOpenSupportFootprintReuse(out session));
                        session!.ResetSource(new(new object(), new object()));
                    }
                    result = binding.StepTowards(state, Vector2.Zero, false, capture.Options.EdgeProbeSeconds, first!);
                }
                if (reuse)
                {
                    Assert.Equal(MovementAvailability.Known, binding.TryAcquire(scope, out var next));
                    using (next) result = binding.StepTowards(state, Vector2.Zero, false, capture.Options.EdgeProbeSeconds, next!);
                }
                return result;
            }
            finally { session?.Dispose(); }
        }

        Vector3 Feet(MovementStepResult result) => result.State.State.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight +
            result.State.Frame.PhysicsOrigin;

        static int[] Bits(Vector3 value) => [BitConverter.SingleToInt32Bits(value.X), BitConverter.SingleToInt32Bits(value.Y),
            BitConverter.SingleToInt32Bits(value.Z)];
    }
}
