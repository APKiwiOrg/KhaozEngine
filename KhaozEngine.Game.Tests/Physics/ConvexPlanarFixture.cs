using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

internal sealed class ConvexPlanarFixture : IDisposable
{
    readonly BepuPhysicsWorld _world = new(Vector3.Zero);
    internal InstalledSweepLeaf Leaf { get; }
    internal ConvexOracleTarget Oracle { get; }

    internal ConvexPlanarFixture(Vector3 half, Pose pose, bool hull = false, Vector3 vertexOffset = default)
    {
        Vector3[] corners = new Vector3[8];
        for (int i = 0; i < 8; i++) corners[i] = new((i & 1) == 0 ? -half.X : half.X,
            (i & 2) == 0 ? -half.Y : half.Y, (i & 4) == 0 ? -half.Z : half.Z);
        if (hull)
            for (int i = 0; i < corners.Length; i++) corners[i] += vertexOffset;
        var handle = _world.AddStatic(hull ? new ConvexHullShape(corners) : new BoxShape(half), pose);
        var raw = Static(_world, handle);
        var installed = Sim(_world).Statics[raw.Handle];
        var geometry = CertifiedCompoundSweepTests.Geometry(_world, handle);
        Assert.True(geometry.TryComposePose(installed.Pose, out var leafPose));
        Assert.True(InstalledPoseOperator.TryCreate(leafPose, out var map));
        geometry.PublishPose(installed.Pose, leafPose, map);
        Assert.True(CapsuleSweepGeometry.TryReadLeaf(Sim(_world).Shapes, raw.Shape, installed.Pose,
            geometry, 0, out var leaf));
        Leaf = leaf;
        Oracle = ConvexOracleTarget.Capture(Sim(_world).Shapes, raw.Shape, installed.Pose);
    }

    public void Dispose() => _world.Dispose();
}
