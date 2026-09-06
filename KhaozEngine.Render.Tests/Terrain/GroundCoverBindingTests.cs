using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

[Collection("AllocSensitive")]
public sealed class GroundCoverBindingTests
{
    static GroundCoverBatch Field() => new(Enumerable.Range(0, 4096).Select(i =>
        new GroundCoverInstance(i % 2 == 0 ? "grass" : "clover", new Vector3(i * .001f, 0f, 0f),
            Matrix4x4.CreateTranslation(i * .001f, 0f, 0f), 0f)).ToArray());

    [Fact]
    public void RepeatedBladesResolveEachModelOnlyOncePerSubmission()
    {
        var comparer = new CountingComparer();
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>(comparer)
        {
            ["grass"] = [new MeshHandle(4, 1)],
            ["clover"] = [new MeshHandle(9, 1)],
        };
        GroundCoverBatch batch = Field();
        var queue = new SceneInstances();
        comparer.HashCalls = 0;

        int drawn = GroundCoverRenderer.Queue(queue, batch, meshes, Vector3.Zero, new GroundCoverRenderOptions());

        Assert.Equal(4096, drawn);
        Assert.Equal(4096, queue.Items.Count);
        Assert.Equal(new MeshHandle(4, 1), queue.Items[0].Mesh);
        Assert.Equal(new MeshHandle(9, 1), queue.Items[4095].Mesh);
        Assert.InRange(comparer.HashCalls, 1, 2);
    }

    [Fact]
    public void BatchBindingsFollowMeshReplacementRemovalAndRestoration()
    {
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>
        {
            ["grass"] = [new MeshHandle(4, 1)],
            ["clover"] = [new MeshHandle(9, 1)],
        };
        GroundCoverBatch batch = Field();
        var queue = new SceneInstances();
        var options = new GroundCoverRenderOptions();
        GroundCoverRenderer.Queue(queue, batch, meshes, Vector3.Zero, options);

        meshes["grass"] = [new MeshHandle(4, 2), new MeshHandle(7, 1)];
        meshes.Remove("clover");
        queue.Begin();
        Assert.Equal(2048, GroundCoverRenderer.Queue(queue, batch, meshes, Vector3.Zero, options));
        Assert.Equal(4096, queue.Items.Count);
        Assert.Equal(new MeshHandle(4, 2), queue.Items[0].Mesh);
        Assert.Equal(new MeshHandle(7, 1), queue.Items[1].Mesh);
        Assert.DoesNotContain(queue.Items, i => i.Mesh.Equals(new MeshHandle(9, 1)));
        Assert.Equal(4.094f, queue.Items[^1].World.M41, 4);

        meshes["clover"] = [new MeshHandle(9, 2)];
        queue.Begin();
        Assert.Equal(4096, GroundCoverRenderer.Queue(queue, batch, meshes, Vector3.Zero, options));
        Assert.Equal(6144, queue.Items.Count);
        Assert.Equal(new MeshHandle(9, 2), queue.Items[^1].Mesh);
    }

    [Fact]
    public void RepeatedSubmissionsDoNotAllocateModelBindings()
    {
        var meshes = new Dictionary<string, IReadOnlyList<MeshHandle>>
        {
            ["grass"] = [new MeshHandle(4, 1)],
            ["clover"] = [new MeshHandle(9, 1)],
        };
        GroundCoverBatch batch = Field();
        var queue = new SceneInstances();
        var options = new GroundCoverRenderOptions();
        void Submit()
        {
            queue.Begin();
            GroundCoverRenderer.Queue(queue, batch, meshes, Vector3.Zero, options);
        }
        for (int i = 0; i < 20; i++) Submit();

        AllocAssert.NoPerCallAllocation("submitting a field with two model bindings", () =>
        {
            for (int i = 0; i < 20; i++) Submit();
        });
        Assert.Equal(4096, queue.Items.Count);
    }

    sealed class CountingComparer : IEqualityComparer<string>
    {
        public int HashCalls;
        public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);
        public int GetHashCode(string value)
        {
            HashCalls++;
            return StringComparer.Ordinal.GetHashCode(value);
        }
    }
}
