using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Issue #393: the prop emit path allocated a closure display class plus a delegate (two, on the
    /// blob-capable overloads) on every call. That is once per layer per loaded chunk per frame, so a full scene was
    /// churning tens of thousands of gen0 allocations a second to carry three fields. The state now travels as a
    /// struct through a generic, so the sink delegates are static and allocated once.
    /// <para>Measured through <see cref="PropRenderer.Queue(SceneInstances, IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, Vector3, float, KhaozEngine.Primitives.Color?, float, IReadOnlyDictionary{string, MeshHandle}, float, float, bool)"/>
    /// rather than <c>DrawProps</c>, because the queue path is the same emit loop with the same sink shape and needs
    /// no GPU device. A warm-up pass first, so the instance buffer has reached its steady-state capacity and the JIT
    /// has resolved the generic instantiation: what is left is the per-call cost, and it must be nothing.</para></summary>
    [Collection("AllocSensitive")]   // a zero-allocation reading measures its neighbours too (#264)
    public sealed class PropRendererAllocationTests
    {
        static Dictionary<string, MeshHandle> Meshes() => new() { ["pine_a"] = new MeshHandle(1), ["rock_a"] = new MeshHandle(2) };

        static Dictionary<string, IReadOnlyList<MeshHandle>> Parts() => new()
        {
            ["pine_a"] = new List<MeshHandle> { new(1), new(2) },
            ["rock_a"] = new List<MeshHandle> { new(3) },
        };

        static List<PropPlacement> Placements(int count)
        {
            var list = new List<PropPlacement>(count);
            for (int i = 0; i < count; i++)
                list.Add(new PropPlacement(i % 2 == 0 ? "pine_a" : "rock_a", i * 0.7f, 2f, i * -0.3f, 1f, 0.1f * i, 0));
            return list;
        }

        [Fact]
        public void Queue_AllocatesNothingPerCall()
        {
            var instances = new SceneInstances();
            Dictionary<string, MeshHandle> meshes = Meshes();
            List<PropPlacement> placements = Placements(200);

            for (int i = 0; i < 4; i++)
            {
                instances.Begin();
                PropRenderer.Queue(instances, placements, meshes, Vector3.Zero, drawRadius: 1000f);
            }
            Assert.Equal(placements.Count, instances.Items.Count);   // not vacuous: every placement really is queued

            instances.Begin();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
            {
                PropRenderer.Queue(instances, placements, meshes, Vector3.Zero, drawRadius: 1000f);
                instances.Begin();
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
        }

        [Fact]
        public void QueueParts_AllocatesNothingPerCall()
        {
            var instances = new SceneInstances();
            Dictionary<string, IReadOnlyList<MeshHandle>> parts = Parts();
            List<PropPlacement> placements = Placements(200);

            for (int i = 0; i < 4; i++)
            {
                instances.Begin();
                PropRenderer.Queue(instances, placements, parts, Vector3.Zero, drawRadius: 1000f);
            }
            Assert.True(instances.Items.Count > placements.Count);   // not vacuous: multi-part, so more than one each

            instances.Begin();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20; i++)
            {
                PropRenderer.Queue(instances, placements, parts, Vector3.Zero, drawRadius: 1000f);
                instances.Begin();
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
        }
    }
}
