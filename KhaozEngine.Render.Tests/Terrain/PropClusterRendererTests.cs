using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public sealed class PropClusterRendererTests
    {
        [Fact]
        public void Apply_rejects_a_stale_generation_and_keeps_the_current_handle()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 2, -1, 0);
            PropClusterCpuBuild stale = rig.Build(key, generation: 1);
            rig.Apply(key, rig.Build(key, generation: 2));

            rig.Apply(key, stale);

            Assert.Equal(2, rig.GenerationOf(key));
            Assert.Equal(1, rig.LiveHandleCount);
            Assert.Equal(0, rig.UnloadCount);
        }

        [Fact]
        public void Apply_replaces_a_same_generation_invalidated_build_and_disposes_the_old_handle()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            rig.Apply(key, rig.Build(key, generation: 4));
            int firstHandle = rig.HandleIndexOf(key);
            rig.Renderer.Invalidate(key);

            rig.Apply(key, rig.Build(key, generation: 4));

            Assert.Equal(1, rig.LiveHandleCount);
            Assert.Equal(1, rig.UnloadCount);
            Assert.NotEqual(firstHandle, rig.HandleIndexOf(key));
        }

        [Fact]
        public void BuildCpu_reuses_an_applied_generation_until_it_is_invalidated()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            rig.Apply(key, rig.Build(key, generation: 7));

            rig.Apply(key, rig.Build(key, generation: 7));

            Assert.Equal(1, rig.MergeAttempts);
            Assert.Equal(1, rig.LoadCount);
            Assert.Equal(0, rig.UnloadCount);
        }

        [Fact]
        public void Cached_generation_refreshes_its_individual_batch_without_handle_churn()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            rig.Apply(key, rig.Build(key, generation: 7));
            var moved = new PropPlacement("oak", 9f, 0f, 1f, 1f, 0f, 0);

            rig.Apply(key, rig.Build(key, generation: 7, moved));
            rig.Renderer.Draw(Vector3.Zero);

            Assert.Equal(9f, rig.LastDrawX);
            Assert.Equal(1, rig.LoadCount);
            Assert.Equal(0, rig.UnloadCount);
        }

        [Fact]
        public void Failed_rebuild_retains_the_last_accepted_handle()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            rig.Apply(key, rig.Build(key, generation: 1));
            int firstHandle = rig.HandleIndexOf(key);
            rig.Renderer.Invalidate(key);
            rig.FailEveryMerge = true;

            rig.Apply(key, rig.Build(key, generation: 2));

            Assert.Equal(1, rig.GenerationOf(key));
            Assert.Equal(firstHandle, rig.HandleIndexOf(key));
            Assert.Equal(1, rig.LiveHandleCount);
            Assert.Equal(0, rig.UnloadCount);
        }

        [Fact]
        public void Failed_new_generation_still_rejects_an_older_completed_build()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            PropClusterCpuBuild older = rig.Build(key, generation: 1);
            rig.Apply(key, rig.Build(key, generation: 1));
            int acceptedHandle = rig.HandleIndexOf(key);
            rig.Renderer.Invalidate(key);
            rig.FailEveryMerge = true;
            rig.Apply(key, rig.Build(key, generation: 2));

            rig.Apply(key, older);

            Assert.Equal(acceptedHandle, rig.HandleIndexOf(key));
            Assert.Equal(0, rig.UnloadCount);
        }

        [Fact]
        public void Initial_build_retries_twice_then_accepts_the_third_attempt()
        {
            using var rig = new PropClusterRig { FailuresRemaining = 2 };
            PropClusterKey key = new("trees", 0, 0, 0);

            rig.Apply(key, rig.Build(key, generation: 1));

            Assert.Equal(3, rig.MergeAttempts);
            Assert.Equal(1, rig.LiveHandleCount);
            Assert.Equal(1, rig.GenerationOf(key));
        }

        [Fact]
        public void Initial_build_stops_after_three_failures_and_logs_once()
        {
            using var rig = new PropClusterRig { FailEveryMerge = true };
            PropClusterKey key = new("trees", 0, 0, 0);

            rig.Apply(key, rig.Build(key, generation: 1));
            rig.Renderer.Draw(Vector3.Zero);

            Assert.Equal(3, rig.MergeAttempts);
            Assert.Equal(0, rig.LiveHandleCount);
            Assert.Equal(1, rig.LogCount);
            Assert.Equal(1f, rig.LastDrawX);
        }

        [Fact]
        public void Unload_and_disposal_are_idempotent()
        {
            var rig = new PropClusterRig();
            PropClusterKey first = new("trees", 0, 0, 0);
            PropClusterKey second = new("trees", 1, 0, 0);
            rig.Apply(first, rig.Build(first, generation: 1));
            rig.Apply(second, rig.Build(second, generation: 1));

            rig.Renderer.Unload(first);
            rig.Renderer.Unload(first);
            rig.Renderer.Dispose();
            rig.Renderer.Dispose();

            Assert.Equal(0, rig.LiveHandleCount);
            Assert.Equal(2, rig.UnloadCount);
        }

        [Fact]
        public void BuildCpu_detaches_the_request_lists_and_contains_no_gpu_handle()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            var placements = new List<PropPlacement> { PropClusterRig.Placement };
            var request = new PropClusterBuildRequest(key, 1, new RectArea(0f, 0f, 64f, 64f), PropClusterRig.Layer, placements);

            PropClusterCpuBuild build = rig.Renderer.BuildCpu(request);
            placements.Clear();

            Assert.Single(build.PlacementBatch);
            Assert.Equal(PropClusterRig.Layer.DrawRadius, build.Layer.DrawRadius);
            Assert.NotNull(build.MergedMesh);
        }

        sealed class PropClusterRig : IDisposable
        {
            public static readonly PropPlacement Placement = new("oak", 1f, 0f, 1f, 1f, 0f, 0);
            public static readonly PropLayer Layer = PropLayer.PlacementLayer(
                    new[] { Placement },
                    new Dictionary<string, MeshHandle> { ["oak"] = new MeshHandle(90) },
                    drawRadius: 500f)
                .WithHlod(
                    new Dictionary<string, GltfMesh> { ["oak"] = MeshPrimitives.Box(1f) },
                    hlodDistance: 100f,
                    weldCell: 0f,
                    crossfadeWidth: 40f);

            readonly FakeBackend _backend = new();
            public readonly PropClusterRenderer Renderer;
            public int MergeAttempts;
            public int FailuresRemaining;
            public bool FailEveryMerge;
            public int LogCount;

            public PropClusterRig()
            {
                Renderer = new PropClusterRenderer(_backend, BuildMerged, (_, _) => LogCount++);
            }

            public int LiveHandleCount => _backend.Live.Count;
            public int LoadCount => _backend.LoadCount;
            public int UnloadCount => _backend.UnloadCount;
            public float LastDrawX => _backend.LastDrawX;

            public PropClusterCpuBuild Build(PropClusterKey key, long generation)
                => Build(key, generation, Placement);

            public PropClusterCpuBuild Build(PropClusterKey key, long generation, PropPlacement placement)
            {
                var request = new PropClusterBuildRequest(
                    key,
                    generation,
                    new RectArea(key.X * 64f, key.Z * 64f, (key.X + 1) * 64f, (key.Z + 1) * 64f),
                    Layer,
                    new[] { placement });
                return Renderer.BuildCpu(request);
            }

            public void Apply(PropClusterKey key, PropClusterCpuBuild build) => Renderer.Apply(key, build);
            public long GenerationOf(PropClusterKey key) => Renderer.GenerationOf(key);
            public int HandleIndexOf(PropClusterKey key) => Renderer.HandleOf(key)!.Value.Index;

            GltfMesh BuildMerged(IReadOnlyList<PropPlacement> placements,
                                 IReadOnlyDictionary<string, GltfMesh> sources,
                                 float weldCell,
                                 out long malformedCornersDropped)
            {
                MergeAttempts++;
                malformedCornersDropped = 0;
                if (FailEveryMerge || FailuresRemaining-- > 0)
                    throw new InvalidOperationException("planned merge failure");
                return PropHlod.BuildMergedMesh(placements, sources, weldCell);
            }

            public void Dispose() => Renderer.Dispose();
        }

        sealed class FakeBackend : IPropClusterRenderBackend
        {
            int _next;
            public readonly HashSet<int> Live = new();
            public int LoadCount;
            public int UnloadCount;
            public float LastDrawX;

            public MeshHandle LoadMesh(GltfMesh mesh)
            {
                int index = ++_next;
                Live.Add(index);
                LoadCount++;
                return new MeshHandle(index);
            }

            public void UnloadMesh(MeshHandle handle)
            {
                Assert.True(Live.Remove(handle.Index));
                UnloadCount++;
            }

            public void DrawProps(IReadOnlyList<PropPlacement> placements, PropLayer layer, Vector3 focus,
                                  float dissolveFloor)
            {
                if (placements.Count > 0) LastDrawX = placements[0].X;
            }

            public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve)
            {
            }
        }
    }
}
