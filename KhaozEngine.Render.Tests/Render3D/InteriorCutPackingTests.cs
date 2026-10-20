using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class InteriorCutPackingTests
{
    [Fact]
    public void PartitionKeepsMotionMaterialAndCasterPoliciesAlignedAfterRetention()
    {
        var mesh = new MeshHandle(4, 2);
        var items = new[]
        {
            new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(99, 0, 0)) { CutInsideInteriors = true },
            new RigidInstanceDraw(mesh, Matrix4x4.Identity) { Motion = MotionKey.From(10) },
            new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(1, 0, 0))
            { CutInsideInteriors = true, Motion = MotionKey.From(11), Dissolve = .4f, InvertShadowDissolve = true },
            new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(2, 0, 0)) { ShadowOnly = true, Motion = MotionKey.From(12) },
            new RigidInstanceDraw(mesh, Matrix4x4.CreateTranslation(3, 0, 0))
            { CutInsideInteriors = true, CastsShadows = false, Motion = MotionKey.From(13), Tint = new Color(1, 0, 0, 1) },
        }.Select(draw => new SceneInstances.Instance(draw)).ToArray();
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var kinds = new List<ShadowCastKind>();
        var shadowOnly = new List<bool>();
        var keys = new List<MotionKey>();
        Scene3D.GroupInstances(items, data, runs, castKinds: kinds,
            retained: new[] { false, true, true, true, true }, shadowOnly: shadowOnly, motionKeys: keys,
            interiorCuts: new InteriorCutInstancePartition());

        Assert.Equal(4u, Assert.Single(runs).Count);
        Assert.Equal(new[] { 1f, 3f, 0f, 2f }, data.Select(item => item.Model.M41));
        Assert.Equal(new[] { 1f, 1f, 0f, 0f }, data.Select(item => item.SpecParams.W));
        Assert.Equal(new ulong[] { 11, 13, 10, 12 }, keys.Select(key => key.Value));
        Assert.Equal(new[] { ShadowCastKind.DissolvingInverted, ShadowCastKind.None, ShadowCastKind.Opaque, ShadowCastKind.Opaque }, kinds);
        Assert.Equal(new[] { false, false, false, true }, shadowOnly);
        Assert.Equal(.4f, data[0].Dissolve.X);
        Assert.Equal(new Vector4(1, 0, 0, 1), data[1].Tint);
    }

    [Fact]
    public void AllMissPartitionsKeepTheOriginalPackedBytes()
    {
        var items = new[]
        {
            new SceneInstances.Instance(new MeshHandle(3, 1), Matrix4x4.CreateTranslation(4, 5, 6), new Color(1, 0, 0, 1)),
            new SceneInstances.Instance(new MeshHandle(2, 1), Matrix4x4.Identity, Color.White),
            new SceneInstances.Instance(new MeshHandle(3, 1), Matrix4x4.CreateScale(2), Color.White),
        };
        var baseline = new List<ModelRenderer.InstanceData>();
        var partitioned = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var actualRuns = new List<Scene3D.MeshRun>();
        Scene3D.GroupInstances(items, baseline, runs);
        Scene3D.GroupInstances(items, partitioned, actualRuns, interiorCuts: new InteriorCutInstancePartition());

        Assert.Equal(runs, actualRuns);
        Assert.True(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(baseline))
            .SequenceEqual(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(partitioned))));
    }
}
