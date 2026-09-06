using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain;

public class GroundCoverRendererTests
{
    static GroundCoverInstance Instance(string id, float x, float rank) => new(
        id, new Vector3(x, 1f, 0f),
        Matrix4x4.CreateRotationZ(0.2f) * Matrix4x4.CreateTranslation(x, 1f, 0f), rank);

    static IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> Meshes() =>
        new Dictionary<string, IReadOnlyList<MeshHandle>>
        {
            ["grass"] = [new MeshHandle(4), new MeshHandle(9)],
        };

    [Fact]
    public void Queue_UsesPrecomputedSurfaceTransformForEveryMeshPart()
    {
        GroundCoverInstance cover = Instance("grass", 2f, 0.1f);
        var queue = new SceneInstances();

        int drawn = GroundCoverRenderer.Queue(queue, [cover], Meshes(), Vector3.Zero,
            new GroundCoverRenderOptions { DrawRadius = 20f });

        Assert.Equal(1, drawn);
        Assert.Equal(2, queue.Items.Count);
        Assert.Equal(new[] { new MeshHandle(4), new MeshHandle(9) }, queue.Items.Select(x => x.Mesh));
        Assert.All(queue.Items, x => Assert.Equal(cover.Transform, x.World));
    }

    [Fact]
    public void Queue_QualityAndDistanceThinningAreStableNestedSubsets()
    {
        GroundCoverInstance[] cover = Enumerable.Range(0, 100)
            .Select(i => Instance("grass", 1f, i / 100f)).ToArray();
        var queue = new SceneInstances();
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes = Meshes();

        int high = GroundCoverRenderer.Queue(queue, cover, meshes, Vector3.Zero,
            new GroundCoverRenderOptions { DrawRadius = 20f, QualityDensity = 0.8f, DistantDensity = 0.4f });
        queue.Begin();
        int low = GroundCoverRenderer.Queue(queue, cover, meshes, Vector3.Zero,
            new GroundCoverRenderOptions { DrawRadius = 20f, QualityDensity = 0.5f, DistantDensity = 0.25f });
        queue.Begin();
        int zero = GroundCoverRenderer.Queue(queue, cover, meshes, Vector3.Zero,
            new GroundCoverRenderOptions { DrawRadius = 20f, QualityDensity = 0f });

        Assert.Equal(80, high);
        Assert.Equal(50, low);
        Assert.Equal(0, zero);
    }

    [Fact]
    public void Queue_DissolvesEachRankBeforeItsDistanceCull()
    {
        var queue = new SceneInstances();
        var options = new GroundCoverRenderOptions
        {
            DrawRadius = 20f,
            FadeBandWidth = 10f,
            QualityDensity = 1f,
            DistantDensity = 0.2f,
        };

        int visible = GroundCoverRenderer.Queue(queue, [Instance("grass", 11.1f, 0.9f)], Meshes(), Vector3.Zero, options);
        float dissolve = Assert.Single(queue.Items.Take(1)).DissolveThreshold;
        queue.Begin();
        int culled = GroundCoverRenderer.Queue(queue, [Instance("grass", 11.5f, 0.9f)], Meshes(), Vector3.Zero, options);

        Assert.Equal(1, visible);
        Assert.InRange(dissolve, 0.01f, 0.99f);
        Assert.Equal(0, culled);
    }

    [Fact]
    public void Queue_DefaultsToNoShadowCastingAndCanOptIn()
    {
        var queue = new SceneInstances();
        GroundCoverInstance cover = Instance("grass", 1f, 0f);
        GroundCoverRenderer.Queue(queue, [cover], Meshes(), Vector3.Zero, new GroundCoverRenderOptions());
        Assert.All(queue.Items, x => Assert.False(x.CastsShadows));

        queue.Begin();
        GroundCoverRenderer.Queue(queue, [cover], Meshes(), Vector3.Zero,
            new GroundCoverRenderOptions { CastsShadows = true });
        Assert.All(queue.Items, x => Assert.True(x.CastsShadows));
    }

    [Fact]
    public void Queue_SkipsUnknownModelsAndKeepsTintAndMaterialDefaults()
    {
        var queue = new SceneInstances();
        int drawn = GroundCoverRenderer.Queue(queue, [Instance("unknown", 1f, 0f)], Meshes(), Vector3.Zero,
            new GroundCoverRenderOptions());
        Assert.Equal(0, drawn);
        Assert.Empty(queue.Items);

        drawn = GroundCoverRenderer.Queue(queue, [Instance("grass", 1f, 0f)], Meshes(), Vector3.Zero,
            new GroundCoverRenderOptions());
        Assert.Equal(1, drawn);
        Assert.All(queue.Items, x =>
        {
            Assert.Equal(Color.White, x.Tint);
            Assert.Equal(Material.None, x.Material);
        });
    }

    [Fact]
    public void Queue_ResolvesModelsOnlyAfterCheapPlacementCulling()
    {
        var queue = new SceneInstances();
        var meshes = new CountingMeshes();
        GroundCoverInstance[] cover =
        [
            Instance("grass", 100f, 0f),
            Instance("grass", 1f, 0.9f),
        ];

        int drawn = GroundCoverRenderer.Queue(queue, cover, meshes, Vector3.Zero,
            new GroundCoverRenderOptions { DrawRadius = 20f, QualityDensity = 0.5f });

        Assert.Equal(0, drawn);
        Assert.Equal(0, meshes.Lookups);
    }

    [Fact]
    public void Queue_AllocatesNothingAfterWarmup()
    {
        GroundCoverInstance[] cover = Enumerable.Range(0, 64)
            .Select(i => Instance("grass", i * 0.1f, i / 64f)).ToArray();
        IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> meshes = Meshes();
        var options = new GroundCoverRenderOptions { DrawRadius = 20f };
        var queue = new SceneInstances();
        GroundCoverRenderer.Queue(queue, cover, meshes, Vector3.Zero, options);
        queue.Begin();

        long before = GC.GetAllocatedBytesForCurrentThread();
        GroundCoverRenderer.Queue(queue, cover, meshes, Vector3.Zero, options);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    sealed class CountingMeshes : IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>
    {
        public int Lookups { get; private set; }
        public IReadOnlyList<MeshHandle> this[string key] => throw new KeyNotFoundException(key);
        public IEnumerable<string> Keys => [];
        public IEnumerable<IReadOnlyList<MeshHandle>> Values => [];
        public int Count => 0;
        public bool ContainsKey(string key) => false;
        public IEnumerator<KeyValuePair<string, IReadOnlyList<MeshHandle>>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, IReadOnlyList<MeshHandle>>>().GetEnumerator();
        public bool TryGetValue(string key, out IReadOnlyList<MeshHandle> value)
        {
            Lookups++;
            value = [];
            return false;
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
