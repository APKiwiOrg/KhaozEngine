using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class RigidInstanceGroupingTests
{
    [Fact]
    public void PackingReusesTheMeshIndexResolvedDuringCounting()
    {
        var comparer = new CountingComparer();
        var map = new Dictionary<(int, int), int>(comparer);
        var items = new SceneInstances.Instance[4096];
        var retained = new bool[items.Length];
        for (int i = 0; i < items.Length; i++)
        {
            items[i] = new(new MeshHandle(i % 4, i % 8 / 4), Matrix4x4.CreateTranslation(i, 0f, 0f), Color.White);
            retained[i] = i % 4 != 0;
        }
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();

        Scene3D.GroupInstances(items, data, runs, map, retained: retained);

        Assert.Equal(3072, data.Count);
        Assert.Equal(8, runs.Count);
        Assert.Equal(new MeshHandle(0, 0), runs[0].Mesh);
        Assert.Equal(0u, runs[0].Count);
        Assert.Equal(1f, data[0].Model.M41);
        Assert.InRange(comparer.HashCalls, 1, items.Length + 8);
    }

    [Fact]
    public void RetentionKeepsFirstSeenMeshOrderAndEachSurvivingInstancesMaterial()
    {
        var items = new SceneInstances.Instance[]
        {
            new(new MeshHandle(5, 1), Matrix4x4.CreateTranslation(100f, 0f, 0f), Color.White),
            new(new MeshHandle(2, 1), Matrix4x4.CreateTranslation(20f, 0f, 0f), Color.White),
            new(new MeshHandle(5, 1), Matrix4x4.CreateTranslation(10f, 0f, 0f), Color.White,
                Material.None, 0.5f, 0.2f, new Vector4(1f, 0.5f, 0f, 1f), false),
            new(new MeshHandle(5, 2), Matrix4x4.CreateTranslation(30f, 0f, 0f), Color.White),
            new(new MeshHandle(5, 1), Matrix4x4.CreateTranslation(11f, 0f, 0f), Color.White),
        };
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var kinds = new List<ShadowCastKind>();
        Scene3D.GroupInstances(items, data, runs, castKinds: kinds,
            retained: new[] { false, true, true, true, true });

        Assert.Equal(3, runs.Count);
        Assert.Equal(new MeshHandle(5, 1), runs[0].Mesh);
        Assert.Equal(new MeshHandle(2, 1), runs[1].Mesh);
        Assert.Equal(new MeshHandle(5, 2), runs[2].Mesh);
        Assert.Equal(2u, runs[0].Count);
        Assert.Equal(10f, data[0].Model.M41);
        Assert.Equal(11f, data[1].Model.M41);
        Assert.Equal(20f, data[2].Model.M41);
        Assert.Equal(30f, data[3].Model.M41);
        Assert.Equal(new Vector2(0.5f, 0.2f), data[0].Dissolve);
        Assert.Equal(new Vector4(1f, 0.5f, 0f, 1f), data[0].Emissive);
        Assert.Equal(new[] { ShadowCastKind.None, ShadowCastKind.Opaque, ShadowCastKind.Opaque, ShadowCastKind.Opaque }, kinds);
    }

    [Fact]
    public void ReusedBuffersDoNotKeepRejectedInstancesOrPreviousRetention()
    {
        var items = new[] { new SceneInstances.Instance(new MeshHandle(7), Matrix4x4.Identity, Color.White) };
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var map = new Dictionary<(int, int), int>();
        var kinds = new List<ShadowCastKind>();
        var cursors = new List<uint>();

        Scene3D.GroupInstances(items, data, runs, map, kinds, new[] { false }, cursors);
        Assert.Empty(data);
        Assert.Empty(kinds);
        Assert.All(runs, run => Assert.Equal(0u, run.Count));

        Scene3D.GroupInstances(items, data, runs, map, kinds, writeCursorScratch: cursors);
        Assert.Single(data);
        Assert.Single(kinds);
        Assert.Equal(1u, Assert.Single(runs).Count);
    }

    [Theory]
    [InlineData(256)]
    [InlineData(4096)]
    public void WarmedGroupingWithMoreThan64MeshRunsAllocatesNothing(int population)
    {
        var items = new List<SceneInstances.Instance>();
        var keep = new bool[population];
        for (int i = 0; i < keep.Length; i++)
        {
            items.Add(new SceneInstances.Instance(new MeshHandle(i % 128), Matrix4x4.Identity, Color.White));
            keep[i] = (i & 1) == 0;
        }
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var map = new Dictionary<(int, int), int>();
        var kinds = new List<ShadowCastKind>();
        var cursors = new List<uint>();
        void Group() => Scene3D.GroupInstances(items, data, runs, map, kinds, keep, cursors);
        for (int i = 0; i < 20; i++) Group();
        AllocAssert.NoPerCallAllocation("grouping 128 mesh runs with retained slots", () =>
        {
            for (int i = 0; i < 20; i++) Group();
        });
        Assert.Equal(population / 2, data.Count);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void OnlyPureTranslationGroundUsesTheTighterAabb(bool ground, bool scaled, bool visible)
    {
        var bounds = new MeshBounds(new Vector3(-10f, -0.1f, -0.1f), new Vector3(10f, 0.1f, 0.1f));
        Matrix4x4 world = Matrix4x4.CreateScale(scaled ? 2f : 1f) * Matrix4x4.CreateTranslation(0f, 2f, 0.5f);
        FrustumPlanes frustum = FrustumPlanes.Extract(Matrix4x4.Identity);
        Assert.Equal(visible, Scene3D.IntersectsMainPass(bounds, world, ground, frustum));
    }

    sealed class CountingComparer : IEqualityComparer<(int, int)>
    {
        public int HashCalls;
        public bool Equals((int, int) x, (int, int) y) => x == y;
        public int GetHashCode((int, int) value)
        {
            HashCalls++;
            return value.GetHashCode();
        }
    }
}
