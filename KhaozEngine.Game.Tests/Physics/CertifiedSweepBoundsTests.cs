using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

public sealed class CertifiedSweepBoundsTests
{
    [Fact]
    public void RotatedBoxFarBoundsAndExcludedUnsupportedSelectionAreIndependent()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(0.5f)), new Pose(new(100, 0, 0), new(0.5f, 0.5f, 0.5f, 0.5f)));
        Clear(Read(world, new(-2, 0, 0), new(4, 0, 0)), 4);
        // A 257-face hull stays unsupported under GS-3 as more leaf families are admitted.
        StaticHandle hull = AddOverFaceCapHull(world, Pose.At(new(100, 0, 0)));
        Refused(Read(world, new(-2, 0, 0), new(4, 0, 0)));
        Clear(Read(world, new(-2, 0, 0), new(4, 0, 0), exclusions: [hull]), 4);
        Mutate(world, simulation =>
        {
            var native = Static(world, hull);
            int index = simulation.Statics[simulation.Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex;
            simulation.BroadPhase.UpdateStaticBounds(index, new(float.NaN), new(101));
        }, hull.Value);
        Refused(Read(world, new(-2, 0, 0), new(4, 0, 0), exclusions: [hull]));
    }

    [Fact]
    public void RotatedBoxInwardBoundsAreRepairedBeforeEvidencePublication()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        for (int i = 0; i < 4; i++)
            world.AddStatic(new BoxShape(new(0.5f)), Pose.At(new(10 + i * 2, 0, 0)));
        var rotation = new Quaternion(0.5f, 0.5f, 0.5f, 0.5f);
        var box = new BoxShape(new(0.5f, 0.25f, 0.125f));
        StaticHandle handle = Install(world, box, new Pose(Vector3.Zero, rotation), (simulation, native) =>
        {
            int index = simulation.Statics[simulation.Statics.HandleToIndex[native.Value]].BroadPhaseIndex;
            simulation.BroadPhase.UpdateStaticBounds(index, new(MathF.BitIncrement(-0.125f), -0.5f, -0.25f), new(0.125f, 0.5f, 0.25f));
        });
        var native = Static(world, handle);
        var sim = Sim(world);
        int broad = sim.Statics[sim.Statics.HandleToIndex[native.Handle.Value]].BroadPhaseIndex;
        var leaf = sim.BroadPhase.StaticTree.Leaves[broad];
        var node = sim.BroadPhase.StaticTree.Nodes[leaf.NodeIndex];
        var bound = leaf.ChildIndex == 0 ? node.A : node.B;
        Assert.True(bound.Min.X <= -0.125f);
        Assert.True(CapsuleTreePreflight.TryValidate(sim.BroadPhase.StaticTree, new(-3), new(3),
            8192, 4096, 128, out _, out _, out _));
        HitContains(Read(world, new(-0.375f, 0, 0), Vector3.Zero), 0, 0);

        int damagedNode = -1;
        BepuPhysics.Trees.Metanode saved = default;
        Install(world, box, new Pose(new(2, 0, 0), rotation), (simulation, added) =>
        {
            int index = simulation.Statics[simulation.Statics.HandleToIndex[added.Value]].BroadPhaseIndex;
            damagedNode = simulation.BroadPhase.StaticTree.Leaves[index].NodeIndex;
            saved = simulation.BroadPhase.StaticTree.Metanodes[damagedNode];
            Assert.True(saved.Parent >= 0);
            simulation.BroadPhase.StaticTree.Metanodes[damagedNode].IndexInParent = 2;
        });
        // Restore structure, but do not manufacture the failed transaction's missing certificate.
        sim.BroadPhase.StaticTree.Metanodes[damagedNode] = saved;
        Refused(Read(world, new(-2, 0, 0), new(4, 0, 0)));
        world.AddStatic(new BoxShape(new(0.5f)), Pose.At(new(30, 0, 0)));
        Refused(Read(world, new(-2, 0, 0), new(4, 0, 0)));
    }

    [Fact]
    public void OperationalRoundingBoundIncludesTheEntireAxisAtEveryPathScale()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var pose = new Pose(new(0.28660256f, 0.2f, 0), new(0, 0, 0.5f, 0.8660254f));
        StaticHandle handle = world.AddStatic(new BoxShape(new(0.125f)), pose);
        var captured = (System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        var native = Static(world, handle);
        Sim(world).Statics.GetDescription(native.Handle, out var installed);
        Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(world).Shapes, native.Shape, installed.Pose, captured[handle.Value]!, 0, out var leaf));
        var map = InstalledPoseOracle.Represented(pose.Orientation).Transpose;
        var translation = InstalledPoseOracle.V.From(pose.Position);
        var axis = map.Apply(InstalledPoseOracle.V.UnitY);
        foreach (float scale in new[] { 0f, 1f / 1024, 1f, 1024f })
        {
            Vector3 centre = pose.Position + new Vector3(-2, 1, 0.5f) * scale, delta = new Vector3(4, -1, 0) * scale;
            const float half = 0.59999996f;
            Assert.True(InstalledSweepBounds.TryOperationalError(leaf, centre, delta, half, out double error));
            var bound = InstalledPoseOracle.R.From(error);
            foreach (var fraction in new[] { InstalledPoseOracle.R.Zero, new InstalledPoseOracle.R(1, 3), InstalledPoseOracle.R.One })
                foreach (float s in new[] { -half, 0, half })
                {
                    var point = InstalledPoseOracle.V.From(centre) + InstalledPoseOracle.V.From(delta) * fraction;
                    var offset = point - translation;
                    var roundedOffset = new InstalledPoseOracle.V(InstalledPoseOracle.Single(offset.X), InstalledPoseOracle.Single(offset.Y), InstalledPoseOracle.Single(offset.Z));
                    var rounded = InstalledPoseOracle.RoundedPoint(map, InstalledPoseOracle.V.From(Vector3.Zero), roundedOffset);
                    var h = InstalledPoseOracle.R.From(s);
                    var result = new InstalledPoseOracle.V(
                        InstalledPoseOracle.Single(rounded.X + InstalledPoseOracle.Single(axis.X * h)),
                        InstalledPoseOracle.Single(rounded.Y + InstalledPoseOracle.Single(axis.Y * h)),
                        InstalledPoseOracle.Single(rounded.Z + InstalledPoseOracle.Single(axis.Z * h)));
                    var expected = map.Apply(offset) + axis * h;
                    var difference = result - expected;
                    Assert.True(difference.X >= -bound && difference.X <= bound);
                    Assert.True(difference.Y >= -bound && difference.Y <= bound);
                    Assert.True(difference.Z >= -bound && difference.Z <= bound);
                }
        }
    }
}
