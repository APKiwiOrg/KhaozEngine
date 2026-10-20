using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedQueryTimePruningTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Routes() => CertifiedStoredBoundsResidualTests.Routes();

    [Theory]
    [MemberData(nameof(Routes))]
    public void DenseSeparatedGeneralContentDoesNotExhaustTheAperture(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        for (int i = 0; i < 4097; i++)
            world.AddStatic(new BoxShape(new(0.25f)), new(new(20, 0, 0), new(0.5f, 0.5f, 0.5f, 0.5f)));
        SameBits(new(CapsuleSweepStatus.Clear, 0.125f, null, 0), Read(world, Vector3.Zero,
            new(0, 0, 0.125f), length: 0, useDimensions: dimensions, exclusions: view ? [Decoy(world)] : null));
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void GeneralTraversalDoesNotCountIdentityLeavesInItsPaddedFringe(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        for (int i = 0; i < 4097; i++)
            world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0.500001f, 0, 0)));
        world.AddStatic(new SphereShape(1), Pose.At(new(4, 0, 0)));
        SameBits(new(CapsuleSweepStatus.Clear, 0.125f, null, 0), Read(world, Vector3.Zero,
            new(0, 0, 0.125f), length: 0, useDimensions: dimensions, exclusions: view ? [Decoy(world)] : null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NearOriginStoredBoundsRetainTheTaskOneForwardEnclosure(bool hull)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        PhysicsShape shape = hull ? new ConvexHullShape([new(-0.25f, -0.5f, -0.125f), new(-0.25f, -0.5f, 0.125f),
            new(-0.25f, 0.5f, -0.125f), new(-0.25f, 0.5f, 0.125f), new(0.25f, -0.5f, -0.125f),
            new(0.25f, -0.5f, 0.125f), new(0.25f, 0.5f, -0.125f), new(0.25f, 0.5f, 0.125f)]) : new BoxShape(new(0.25f, 0.5f, 0.125f));
        var handle = world.AddStatic(shape, new(new(0.5f, 0.25f, 0.125f), new(0.5f, 0.5f, 0.5f, 0.5f)));
        var geometry = CertifiedCompoundSweepTests.Geometry(world, handle);
        var native = Static(world, handle);
        Sim(world).Statics.GetDescription(native.Handle, out var installed);
        Assert.True(geometry.TryComposePose(installed.Pose, out var pose));
        Assert.True(InstalledPoseOperator.TryCreate(pose, out var map));
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        // Independently retain Task 1's affine and rounded-forward construction. The
        // owning repair must not add a query-domain pad to this exact enclosure.
        for (int i = 0; i < geometry.VertexCount; i++)
        {
            Vector3 point = geometry.Vertex(i);
            var affine = map.Point(point, exactZeroProducts: true);
            var rounded = RepresentedGeometryTransforms.PosePoint(new Pose(pose.Position, pose.Orientation), point);
            min = Vector3.Min(min, new(InstalledSweepBounds.Down(Math.Min(affine.X.Bounds.Lower, rounded.X.Lower)),
                InstalledSweepBounds.Down(Math.Min(affine.Y.Bounds.Lower, rounded.Y.Lower)), InstalledSweepBounds.Down(Math.Min(affine.Z.Bounds.Lower, rounded.Z.Lower))));
            max = Vector3.Max(max, new(InstalledSweepBounds.Up(Math.Max(affine.X.Bounds.Upper, rounded.X.Upper)),
                InstalledSweepBounds.Up(Math.Max(affine.Y.Bounds.Upper, rounded.Y.Upper)), InstalledSweepBounds.Up(Math.Max(affine.Z.Bounds.Upper, rounded.Z.Upper))));
        }
        Mutate(world, sim => sim.BroadPhase.UpdateStaticBounds(
            sim.Statics[sim.Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex, min, max), handle.Value);
        var tree = Sim(world).BroadPhase.StaticTree;
        int index = Sim(world).Statics[Sim(world).Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex;
        var link = tree.Leaves[index];
        var node = tree.Nodes[link.NodeIndex];
        var stored = link.ChildIndex == 0 ? node.A : node.B;
        Assert.Equal(min, stored.Min);
        Assert.Equal(max, stored.Max);
    }

    [Fact]
    public void GrimhollowScaleOperationalPadStaysBelowOneCentimetre()
    {
        // Conservative maxima for small props across the entire admitted pose range.
        // Both original query operands reach 4096 m, without endpoint cancellation
        // reducing their error account. The preimage excess is separately included.
        var bounds = new CapsuleSweepPruningBounds(2048, 2048, 16384, 1d / 8192, 2,
            1.75, 1.5, 1.75, 1 + 1d / 32768, 1d / 4096, true);
        Assert.True(CapsuleSweepPruning.TryQueryPad(new(4096), 0.3f, 0.75f, new(-4096), bounds, out double pad));
        Assert.InRange(pad, 0, Math.BitDecrement(0.01));
        output.WriteLine($"Grimhollow-scale query pad {pad:R} m");
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void OperationalPreimageExcessIsCoveredWithoutInflatingTheStoredBox(bool view, bool dimensions)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Quaternion rotation = new(0.4597005546092987f, 0.6279627680778503f, 0.6279627680778503f, 0);
        Assert.True(InstalledPoseOracle.InBand(rotation));
        var handle = world.AddStatic(new BoxShape(new(2048)), new(Vector3.Zero, rotation));
        var transpose = InstalledPoseOracle.Represented(rotation).Transpose;
        var exactVertex = transpose.Inverse(InstalledPoseOracle.V.From(new(-2048, 2048, 2048)));
        Vector3 vertex = new(InstalledPoseOracle.Single(exactVertex.X).AsSingle(), InstalledPoseOracle.Single(exactVertex.Y).AsSingle(),
            InstalledPoseOracle.Single(exactVertex.Z).AsSingle());
        Vector3 centre = vertex + new Vector3(0.2998f, 0, 0);
        var local = InstalledPoseOracle.RoundedPoint(transpose, InstalledPoseOracle.V.Zero, InstalledPoseOracle.V.From(centre));
        var excess = local - InstalledPoseOracle.V.From(new(-2048, 2048, 2048));
        Assert.True(excess.LengthSquared < InstalledPoseOracle.R.From(0.3f).Square());
        var geometry = CertifiedCompoundSweepTests.Geometry(world, handle);
        var sim = Sim(world);
        var native = Static(world, handle).Handle;
        int index = sim.Statics[sim.Statics.HandleToIndex[native.Value]].BroadPhaseIndex;
        var link = sim.BroadPhase.StaticTree.Leaves[index];
        var node = sim.BroadPhase.StaticTree.Nodes[link.NodeIndex];
        var stored = link.ChildIndex == 0 ? node.A : node.B;
        Assert.True(CapsuleSweepAperture.TryCreate(centre, 0.3f, 0.75f, new(0, 0, 0.125f), out var min, out _));
        Assert.True(CapsuleSweepPruning.TryQueryPad(centre, 0.3f, 0.75f, new(0, 0, 0.125f), geometry.PruningMetrics,
            out double rounding, includePreimage: false));
        Assert.True(min.X > stored.Max.X + rounding);
        Assert.True(geometry.PruningMetrics.PreimageExcess > 0.005);
        Refused(Read(world, centre, new(0, 0, 0.125f), radius: 0.3f, length: 1.5f,
            useDimensions: dimensions, exclusions: view ? [Decoy(world)] : null));
    }

    [Fact]
    public void SelectedMagnitudeBoundsTrackPoseRemovalExclusionsAndMobility()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var largest = world.AddStatic(new SphereShape(1), Pose.At(new(1024, 0, 0)));
        var selected = world.AddStatic(new SphereShape(1), Pose.At(new(32, 0, 0)));
        var selection = (CapsuleSweepSelection)Field(world, "_sweepSelection");
        var reverse = (Dictionary<int, int>)Field(world, "_reverseHandles");
        var exclusions = selection.CreateExclusions([largest], reverse);
        Assert.Equal(1024, selection.Bounds(QueryMobility.Statics, null).Root);
        Assert.Equal(32, selection.Bounds(QueryMobility.Statics, exclusions).Root);
        var native = Static(world, selected).Handle;
        Mutate(world, sim =>
        {
            sim.Statics.GetDescription(native, out var description);
            description.Pose.Position = new(128, 0, 0);
            sim.Statics.ApplyDescription(native, description);
        }, selected.Value);
        Assert.Equal(128, selection.Bounds(QueryMobility.Statics, exclusions).Root);
        world.RemoveStatic(largest);
        Assert.Equal(128, selection.Bounds(QueryMobility.Statics, null).Root);
        var body = world.AddDynamic(new SphereShape(1), Pose.At(new(512, 0, 0)), new DynamicBodyDescription(1));
        Assert.Equal(512, selection.Bounds(QueryMobility.All, exclusions).Root);
        Assert.Equal(128, selection.Bounds(QueryMobility.Statics, exclusions).Root);
        world.RemoveDynamic(body);
        Assert.Equal(128, selection.Bounds(QueryMobility.All, exclusions).Root);
        world.RemoveStatic(selected);
        Assert.False(selection.Bounds(QueryMobility.All, exclusions).IsResolved);
    }

    static StaticHandle Decoy(BepuPhysicsWorld world) => world.AddStatic(new BoxShape(new(0.25f)), Pose.At(new(0, 10, 0)));
}
