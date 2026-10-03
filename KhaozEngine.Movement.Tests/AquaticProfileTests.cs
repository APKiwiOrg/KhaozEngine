using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class AquaticProfileTests
{
    private const float Dt = 1f / 30f;
    private static readonly MoveTuning Duck = SwimTraversalProbeTests.Duck;
    private static readonly GroundProfileOptions AquaticOptions = new() { Aquatic = true };
    private static readonly float FloatY = 0f - Duck.SwimSurfaceSubmersionFraction * (2f * Duck.CapsuleHalfHeight);

    // 16 by 8 cells of 0.25 m. Column x index 8 is centred at x 0.125.
    private static readonly PhysicsNavBakeOptions Wet = new(-2f, -1f, 2f, 1f, 0.25f, 2f, 5f, 0.8f, 256, 1024)
    {
        SampleWater = true,
    };

    [Fact]
    public void SteepChannelRoutesAcrossTheFloatLayer()
    {
        using BepuPhysicsWorld world = ChannelWorld();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(ChannelContext(world), Wet, _ => 0u);
        GroundNavigation ground = capture.BuildProfile(Duck, default);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, AquaticOptions);

        NavPath dry = ground.Planner.FindPath(new Vector3(-1.375f, -0.5f, 0.125f), new Vector3(1.375f, -0.5f, 0.125f),
            Duck.CapsuleRadius, PathQueryBudget.Default);
        NavPath wet = aquatic.Planner.FindPath(new Vector3(-1.375f, FloatY, 0.125f), new Vector3(1.375f, FloatY, 0.125f),
            Duck.CapsuleRadius, PathQueryBudget.Default);

        Assert.False(ground.Aquatic);
        Assert.True(aquatic.Aquatic);
        Assert.NotEqual(NavPathStatus.Complete, dry.Status);
        Assert.Equal(NavPathStatus.Complete, wet.Status);
        Assert.True(wet.Waypoints.Count >= 3, $"The route has only {wet.Waypoints.Count} waypoints.");
        for (int i = 1; i < wet.Waypoints.Count - 1; i++)
            Assert.Equal(Bits(FloatY), Bits(HeightOf(aquatic, wet.Waypoints[i])));
    }

    [Fact]
    public void GroundProfileIgnoresWaterEntries()
    {
        using BepuPhysicsWorld world = ChannelWorld();
        GroundMoveContext context = ChannelContext(world);
        using PhysicsNavBake dry = PhysicsNavBake.Capture(context, Wet with { SampleWater = false }, _ => 0u);
        using PhysicsNavBake wet = PhysicsNavBake.Capture(context, Wet, _ => 0u);
        Assert.True(dry.Columns.Water.IsEmpty);
        Assert.False(wet.Columns.Water.IsEmpty);

        BakeEquivalence.AssertEquivalent(dry.BuildProfile(Duck, default), wet.BuildProfile(Duck, default),
            ignoreSampleWater: true);
    }

    [Fact]
    public void AquaticProfileRequiresSampledWater()
    {
        using BepuPhysicsWorld world = ChannelWorld();
        using PhysicsNavBake dry = PhysicsNavBake.Capture(ChannelContext(world), Wet with { SampleWater = false }, _ => 0u);

        Assert.False(GroundProfileOptions.Default.Aquatic);
        Assert.Equal("options", Assert.Throws<ArgumentException>(() => dry.BuildProfile(Duck, default, AquaticOptions)).ParamName);
        Assert.False(dry.BuildProfile(Duck, default, GroundProfileOptions.Default).Aquatic);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(0.7f)]
    public void AquaticProfileRefusesSwimFractionsOutOfOrder(float submersion)
    {
        using BepuPhysicsWorld world = ChannelWorld();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(ChannelContext(world), Wet, _ => 0u);
        MoveTuning tuning = Duck with { SwimSurfaceSubmersionFraction = submersion };

        Assert.Equal("tuning", Assert.Throws<ArgumentException>(() => capture.BuildProfile(tuning, default, AquaticOptions)).ParamName);
        Assert.False(capture.BuildProfile(tuning, default).Aquatic);
    }

    [Fact]
    public void SubmergedOverhangClampsHeadroomToZero()
    {
        using BepuPhysicsWorld world = ChannelWorld();
        // Underside at -1, between the bed at -2 and the float line, top above the water over x in [-0.25, 0.25].
        world.AddStatic(new BoxShape(new Vector3(0.25f, 0.6f, 4f)), Pose.At(new Vector3(0f, -0.4f, 0f)));
        using PhysicsNavBake capture = PhysicsNavBake.Capture(ChannelContext(world), Wet, _ => 0u);

        PhysicsNavColumns derived = AquaticColumns.Derive(capture.Columns, Duck);
        ReadOnlySpan<PhysicsNavSurface> column = derived.GetColumn(8, 4);
        Assert.Equal(2, column.Length);
        Assert.True(derived.IsFloat(8, 4, 0));
        Assert.False(derived.IsFloat(8, 4, 1));
        Assert.Equal(Bits(FloatY), Bits(column[0].Height));
        Assert.Equal(0f, column[0].Headroom);

        GroundNavigation aquatic = capture.BuildProfile(Duck, default, AquaticOptions);
        Assert.False(HasOpenNode(aquatic, 8, 4, FloatY), "A float node sits under the submerged overhang.");
        Assert.True(HasOpenNode(aquatic, 2, 4, FloatY), "The open shelf has no float node.");
    }

    [Fact]
    public void FloatNodesAreIdentifiedAfterLayering()
    {
        using BepuPhysicsWorld world = ChannelWorld();
        // A dry deck over the shelf, top at 1, underside at 0.6, over x in [-1.75, -0.75].
        world.AddStatic(new BoxShape(new Vector3(0.5f, 0.2f, 4f)), Pose.At(new Vector3(-1.25f, 0.8f, 0f)));
        var sampled = new List<Vector3>();
        var context = new GroundMoveContext(ChannelGround, physics: world, medium: (x, z, feetY) =>
        {
            sampled.Add(new Vector3(x, feetY, z));
            return ChannelMedium(x, z, feetY);
        });
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Wet, _ => 0u);
        sampled.Clear();

        GroundNavigation aquatic = capture.BuildProfile(Duck, default, AquaticOptions);

        Assert.True(HasOpenNode(aquatic, 3, 4, 1f), "No deck node was proven.");
        Assert.True(HasOpenNode(aquatic, 3, 4, FloatY), "No float node under the deck was proven.");
        Assert.True(HasOpenNode(aquatic, 8, 4, FloatY), "No float node over the channel was proven.");
        Assert.DoesNotContain(sampled, s => s.Y > 0.5f);
        Assert.Contains(sampled, s => s.X == 0.125f && s.Z == 0.125f && MathF.Abs(s.Y - FloatY) < 0.01f);
    }

    [Fact]
    public void FloatingFeetResolveOntoTheGraph()
    {
        using BepuPhysicsWorld world = ChannelWorld();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(ChannelContext(world), Wet, _ => 0u);
        GroundNavigation ground = capture.BuildProfile(Duck, default);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, AquaticOptions);
        Vector3 from = new(-1.375f, FloatY, 0.125f), to = new(1.375f, FloatY, 0.125f);

        Assert.True(aquatic.AllowsSegment(from, to));
        Assert.True(aquatic.AllowsSegment(to, from));
        Assert.False(ground.AllowsSegment(from, to));
    }

    [Fact]
    public void SwimFractionMismatchIsRefused()
    {
        using BepuPhysicsWorld world = ChannelWorld();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(ChannelContext(world), Wet, _ => 0u);
        GroundNavigation ground = capture.BuildProfile(Duck, default);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, AquaticOptions);
        MoveTuning[] mismatched =
        [
            Duck with { SwimEnterDepthFraction = 0.66f },
            Duck with { SwimExitDepthFraction = 0.56f },
            Duck with { SwimSurfaceSubmersionFraction = 0.61f },
        ];

        aquatic.ValidateTuning(Duck);
        foreach (MoveTuning tuning in mismatched)
        {
            Assert.Throws<ArgumentException>(() => aquatic.ValidateTuning(tuning));
            ground.ValidateTuning(tuning);
        }
    }

    [Fact]
    public void BankConnectsLandToTheFloatLayer()
    {
        using BepuPhysicsWorld world = SwimTraversalProbeTests.RampWorld();
        using PhysicsNavBake capture = PhysicsNavBake.Capture(SwimTraversalProbeTests.RampContext(world), Bank, _ => 0u);
        GroundNavigation aquatic = capture.BuildProfile(Duck, default, AquaticOptions);

        NavPath outward = aquatic.Planner.FindPath(Land, Water, Duck.CapsuleRadius, PathQueryBudget.Default);
        NavPath inward = aquatic.Planner.FindPath(Water, Land, Duck.CapsuleRadius, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, outward.Status);
        Assert.Equal(NavPathStatus.Complete, inward.Status);
        (int intoWater, int ontoBank) = MixedExits(aquatic);
        Assert.True(intoWater > 0, "No exit leads from a bank node onto the float layer.");
        Assert.True(ontoBank > 0, "No exit leads from the float layer onto a bank node.");
    }

    [Fact]
    public void BankEdgePastTheBudgetIsRefusedCleanly()
    {
        // A slow zone: wading and swimming at a fifth of their pace. Dry edges need about 12 steps and float edges
        // along an axis, like the shoreward swim onto a bank node, about 16. Wading out to the float layer needs
        // several times that, so at 20 steps only the wading direction runs past the budget.
        using BepuPhysicsWorld world = SwimTraversalProbeTests.RampWorld();
        GroundMoveContext context = SwimTraversalProbeTests.RampContext(world, zoneScale: 0.2f);
        using PhysicsNavBake tight = PhysicsNavBake.Capture(context, Bank with { MaxEdgeProbeSteps = 20 }, _ => 0u);
        using PhysicsNavBake ample = PhysicsNavBake.Capture(context, Bank with { MaxEdgeProbeSteps = 400 }, _ => 0u);
        GroundNavigation cut = tight.BuildProfile(Duck, default, AquaticOptions);
        GroundNavigation joined = ample.BuildProfile(Duck, default, AquaticOptions);

        (int cutInto, int cutOnto) = MixedExits(cut);
        Assert.Equal(0, cutInto);
        Assert.True(cutOnto > 0, "The shoreward swim within the budget was refused too.");
        Assert.NotEqual(NavPathStatus.Complete, cut.Planner.FindPath(Land, Water, Duck.CapsuleRadius, PathQueryBudget.Default).Status);
        Assert.Equal(NavPathStatus.Complete, cut.Planner.FindPath(Water, Land, Duck.CapsuleRadius, PathQueryBudget.Default).Status);
        Assert.Equal(NavPathStatus.Complete, cut.Planner.FindPath(Land, Land with { Z = -0.375f }, Duck.CapsuleRadius,
            PathQueryBudget.Default).Status);
        Assert.Equal(NavPathStatus.Complete, cut.Planner.FindPath(Water, Water with { Z = -0.375f }, Duck.CapsuleRadius,
            PathQueryBudget.Default).Status);
        Assert.True(MixedExits(joined).IntoWater > 0, "The ample budget joins no bank edge into the water.");
        Assert.Equal(NavPathStatus.Complete, joined.Planner.FindPath(Land, Water, Duck.CapsuleRadius, PathQueryBudget.Default).Status);
        Assert.Equal(NavPathStatus.Complete, joined.Planner.FindPath(Water, Land, Duck.CapsuleRadius, PathQueryBudget.Default).Status);
    }

    [Fact]
    public void DryNodeAtTheWaterLineKeepsItsGroundEdges()
    {
        // Submersion and exit 0 put the float height on the water surface, here exactly the deck top.
        MoveTuning tuning = Duck with { SwimSurfaceSubmersionFraction = 0f, SwimExitDepthFraction = 0f };
        using BepuPhysicsWorld world = SwimTraversalProbeTests.PoolWorld();
        world.AddStatic(new BoxShape(new Vector3(1f, 0.1f, 1f)), Pose.At(new Vector3(0f, -1f, 0f)));
        var probe = new PhysicsColumnProbe(world)
        {
            ProbeHeight = Wet.ProbeHeight,
            ProbeRange = Wet.ProbeRange,
            MaxSlopeRadians = Wet.MaxSlopeRadians,
            GroundMobility = QueryMobility.Statics,
        };
        var context = new GroundMoveContext((x, z) => SwimTraversalProbeTests.InPit(x, z) ? -2f : 0f, physics: world,
            medium: (x, z, feetY) =>
            {
                Span<ColumnSurface> found = stackalloc ColumnSurface[8];
                int count = probe.Sample(x, z, found);
                float top = count > 0 ? found[count - 1].Height : float.NegativeInfinity;
                return new MovementMedium(top, feetY < top);
            });
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, Wet, _ => 0u);
        const int X = 8, Z = 4;
        PhysicsNavSurface[] captured = capture.Columns.GetColumn(X, Z).ToArray();
        Assert.Equal(2, captured.Length);
        Assert.Contains(capture.Columns.Water.ToArray(), w => w.Cell == Z * capture.Columns.Width + X &&
            Bits(w.SurfaceY) == Bits(captured[1].Height));

        PhysicsNavColumns derived = AquaticColumns.Derive(capture.Columns, tuning);
        GroundNavigation ground = capture.BuildProfile(tuning, default);
        GroundNavigation aquatic = capture.BuildProfile(tuning, default, AquaticOptions);

        Assert.Equal(2, derived.GetColumn(X, Z).Length);
        Assert.False(derived.IsFloat(X, Z, 0));
        Assert.False(derived.IsFloat(X, Z, 1));
        int deck = DeckLayer(ground, X, Z, captured[1].Height);
        byte exits = ground.Graph.Layers[deck].ExitMask(X, Z);
        Assert.NotEqual(0, exits);
        Assert.Equal(exits, aquatic.Graph.Layers[DeckLayer(aquatic, X, Z, captured[1].Height)].ExitMask(X, Z));
    }

    [Fact]
    public void SwimPaceUsesSwimSpeedWhileSwimming()
    {
        var context = new GroundMoveContext((x, z) => SwimTraversalProbeTests.InPit(x, z) ? -2f : 0f,
            medium: (x, z, feetY) => SwimTraversalProbeTests.InPit(x, z) ? new MovementMedium(0f, feetY < 0f, 0.8f) : MovementMedium.Dry);
        var swimming = new MoveState
        {
            Position = new Vector3(0f, FloatY + Duck.CapsuleHalfHeight, 0f),
            Swimming = true,
            SpeedScale = 1.5f,
        };
        var walking = new MoveState { Position = new Vector3(3f, Duck.CapsuleHalfHeight, 0f), Grounded = true, SpeedScale = 1.5f };

        Assert.Equal(Duck.SwimSpeed * 0.8f * 1.5f * Dt, SwimPace.Bound(swimming, Duck, false, Dt, context));
        Assert.Equal(Duck.SwimSpeed * 0.8f * 1.5f * Dt, SwimPace.Bound(swimming, Duck, true, Dt, context));
        Assert.Equal(RangeApproachCore.TravelBound(walking, Duck, false, Dt, context),
            SwimPace.Bound(walking, Duck, false, Dt, context));
        Assert.Equal(Duck.WalkSpeed * 1.5f * Dt, SwimPace.Bound(walking, Duck, false, Dt, context));
        Assert.Equal(Duck.RunSpeed * 1.5f * Dt, SwimPace.Bound(walking, Duck, true, Dt, context));

        var hostile = new GroundMoveContext((_, _) => -2f, medium: (_, _, feetY) => new MovementMedium(0f, feetY < 0f, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => SwimPace.Bound(swimming, Duck, false, Dt, hostile));
    }

    // 28 by 8 cells of 0.25 m over the bank of SwimTraversalProbeTests.RampWorld, at the default edge budget. Columns
    // from x 4.125 on are swim-deep for the duck.
    private static readonly PhysicsNavBakeOptions Bank = new(-1f, -1f, 6f, 1f, 0.25f, 2f, 5f, 0.8f, 256, 2048)
    {
        SampleWater = true,
    };

    private static readonly Vector3 Land = new(-0.625f, 0f, 0.125f);
    private static readonly Vector3 Water = new(5.375f, FloatY, 0.125f);

    // Exits from a non-float node to a float node, and from a float node to a non-float node. A node floats when the
    // surface of its derived column at its height is flagged as a float.
    private static (int IntoWater, int OntoBank) MixedExits(GroundNavigation navigation)
    {
        PhysicsNavColumns columns = navigation.Footprint.Columns;
        (int X, int Z)[] directions = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];
        int into = 0, onto = 0;
        for (int layer = 0; layer < navigation.Space.Layers.Count; layer++)
        {
            NavGrid grid = navigation.Space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    byte exits = navigation.Graph.Layers[layer].ExitMask(x, z);
                    for (int d = 0; d < directions.Length; d++)
                    {
                        if ((exits & (1 << d)) == 0) continue;
                        int nx = x + directions[d].X, nz = z + directions[d].Z;
                        bool from = Floats(columns, grid, x, z), to = Floats(columns, grid, nx, nz);
                        if (!from && to) into++;
                        if (from && !to) onto++;
                    }
                }
        }
        return (into, onto);
    }

    private static bool Floats(PhysicsNavColumns columns, NavGrid grid, int x, int z)
    {
        uint height = Bits(grid.SurfaceHeightAt(x, z)!.Value);
        ReadOnlySpan<PhysicsNavSurface> column = columns.GetColumn(x, z);
        for (int i = 0; i < column.Length; i++)
            if (Bits(column[i].Height) == height) return columns.IsFloat(x, z, i);
        return false;
    }

    private static int DeckLayer(GroundNavigation navigation, int x, int z, float height)
    {
        for (int layer = 0; layer < navigation.Space.Layers.Count; layer++)
            if (navigation.Space.Layers[layer].SurfaceHeightAt(x, z) is float y && Bits(y) == Bits(height)) return layer;
        throw new InvalidOperationException("No layer holds the deck node.");
    }

    internal static float ChannelGround(float x, float z) => MathF.Abs(x) < 0.5f ? -2f : -0.5f;

    internal static MovementMedium ChannelMedium(float x, float z, float feetY) => new(0f, feetY < 0f);

    private static GroundMoveContext ChannelContext(IPhysicsWorld world) =>
        new(ChannelGround, physics: world, medium: ChannelMedium);

    /// <summary>Water at 0 over shelves at -0.5 and a 1 m wide channel along Z whose vertical walls drop to a bed at
    /// -2. Every column is swim-deep for the duck.</summary>
    internal static BepuPhysicsWorld ChannelWorld()
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(3.75f, 0.1f, 8f)), Pose.At(new Vector3(-4.25f, -0.6f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(3.75f, 0.1f, 8f)), Pose.At(new Vector3(4.25f, -0.6f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(0.5f, 0.1f, 8f)), Pose.At(new Vector3(0f, -2.1f, 0f)));
        return world;
    }

    private static float HeightOf(GroundNavigation navigation, NavWaypoint waypoint)
    {
        NavGrid grid = navigation.Space.Layers[waypoint.Layer];
        (int x, int z) = grid.CellOf(waypoint.Position.X, waypoint.Position.Y);
        return grid.SurfaceHeightAt(x, z)!.Value;
    }

    private static bool HasOpenNode(GroundNavigation navigation, int x, int z, float height) =>
        navigation.Space.Layers.Any(grid => grid.ClearanceAt(x, z) != 0 && grid.SurfaceHeightAt(x, z) is float y &&
            MathF.Abs(y - height) < 0.01f);

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);
}
