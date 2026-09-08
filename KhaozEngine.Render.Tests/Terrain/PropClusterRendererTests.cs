using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
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
        public void Failed_rebuild_adopts_current_individual_placements_but_keeps_the_accepted_hlod_handle()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            rig.Apply(key, rig.Build(key, generation: 1));
            int acceptedHandle = rig.HandleIndexOf(key);
            rig.Renderer.Invalidate(key);
            rig.FailEveryMerge = true;
            var stump = new PropPlacement("stump", 9f, 0f, 1f, 1f, 0f, 0);

            rig.Apply(key, rig.Build(key, generation: 2, stump));
            rig.Renderer.Draw(new Vector3(32f, 0f, 32f));

            Assert.Equal(9f, rig.LastDrawX);
            Assert.Equal(acceptedHandle, rig.HandleIndexOf(key));
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
        public void Unload_rejects_a_completed_pre_unload_build()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            PropClusterCpuBuild completed = rig.Build(key, generation: 1);

            rig.Renderer.Unload(key);
            rig.Apply(key, completed);
            rig.Renderer.Draw(Vector3.Zero);

            Assert.Equal(0, rig.LoadCount);
            Assert.Equal(0, rig.LiveHandleCount);
            Assert.Null(rig.Renderer.HandleOf(key));
            Assert.Equal(0, rig.DrawCount);
        }

        [Fact]
        public void Fresh_build_after_unload_reopens_the_key()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            PropClusterCpuBuild retired = rig.Build(key, generation: 1);
            rig.Renderer.Unload(key);
            rig.Apply(key, retired);

            rig.Apply(key, rig.Build(key, generation: 1));

            Assert.Equal(1, rig.LoadCount);
            Assert.Equal(1, rig.LiveHandleCount);
        }

        [GpuFact]
        public void Decor_first_then_gameplay_refreshes_props_without_replacing_the_hlod_handle()
        {
            const float chunkSize = 64f;
            var placement = new PropPlacement("oak", 8f, 1f, 8f, 1f, 0f, 0);
            GltfMesh mesh = MeshPrimitives.Box(1f);
            Scene3DChunkSink sink = null!;
            bool handleReused = false;
            int gameplayProps = 0;
            RenderFrameStats stats = default;

            Render3DSnapshot.Capture(64, 64,
                setup: scene =>
                {
                    scene.FrustumCulling = false;
                    scene.Post.Starfield = false;
                    scene.Post.Outline = false;
                    var meshes = new Dictionary<string, MeshHandle> { ["oak"] = scene.LoadMesh(mesh) };
                    var sources = new Dictionary<string, GltfMesh> { ["oak"] = mesh };
                    PropLayer layer = PropLayer.PlacementLayer(new[] { placement }, meshes, drawRadius: 500f)
                        .WithHlod(sources, hlodDistance: 100f, weldCell: 0f);
                    sink = new Scene3DChunkSink(scene, Flat(1f), new[] { layer }, chunkSize);
                    var coord = new ChunkCoord(0, 0);
                    object handle = sink.Load(coord, lod: 2, ring: ChunkRing.Decor);
                    var load = (Scene3DChunkSink.ChunkLoad)handle;
                    MeshHandle before = load.HlodMeshHandles![0]!.Value;

                    sink.ReLod(coord, handle, lod: 0, ring: ChunkRing.Gameplay);

                    MeshHandle after = load.HlodMeshHandles![0]!.Value;
                    handleReused = before.Index == after.Index && before.Generation == after.Generation;
                    gameplayProps = load.LayerProps[0].Count;
                    scene.Camera.Frame(new Vector3(8f, 3f, 8f), new Vector3(20f, 12f, 20f));
                },
                drawFrame: scene =>
                {
                    sink.Draw(new Vector3(8f, 1f, 8f));
                    stats = scene.LastFrameStats;
                },
                frames: 2);

            Assert.True(handleReused);
            Assert.Equal(1, gameplayProps);
            Assert.Equal(2, stats.Instances);
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

        [Fact]
        public void Merged_hlod_uses_an_ordinary_exit_fade_and_stops_at_the_exact_draw_radius()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            PropLayer layer = PropClusterRig.ExitLayer(drawRadius: 500f, fadeWidth: 40f,
                                                       hlodDistance: 100f, hlodWidth: 40f);
            rig.Apply(key, rig.Build(key, generation: 1, layer));

            rig.ResetDraws();
            rig.Renderer.Draw(Focus(460f));
            Assert.Equal(1, rig.MergedDrawCount);
            Assert.Equal(0f, rig.LastMergedDissolve);
            Assert.False(rig.LastMergedInvertShadow);

            rig.ResetDraws();
            rig.Renderer.Draw(Focus(480f));
            Assert.Equal(1, rig.MergedDrawCount);
            Assert.Equal(0.5f, rig.LastMergedDissolve, 5);
            Assert.False(rig.LastMergedInvertShadow);

            rig.ResetDraws();
            rig.Renderer.Draw(Focus(500f));
            Assert.Equal(0, rig.MergedDrawCount);
        }

        [Fact]
        public void Merged_hlod_entrance_keeps_its_inverted_shadow_dissolve()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            PropLayer layer = PropClusterRig.ExitLayer(drawRadius: 500f, fadeWidth: 40f,
                                                       hlodDistance: 100f, hlodWidth: 40f);
            rig.Apply(key, rig.Build(key, generation: 1, layer));

            rig.ResetDraws();
            rig.Renderer.Draw(Focus(100f));

            Assert.Equal(1, rig.MergedDrawCount);
            Assert.Equal(0.5f, rig.LastMergedDissolve, 5);
            Assert.True(rig.LastMergedInvertShadow);
        }

        [Fact]
        public void Exit_fade_takes_precedence_when_it_overlaps_the_hlod_entrance()
        {
            using var rig = new PropClusterRig();
            PropClusterKey key = new("trees", 0, 0, 0);
            PropLayer layer = PropClusterRig.ExitLayer(drawRadius: 120f, fadeWidth: 40f,
                                                       hlodDistance: 100f, hlodWidth: 40f);
            rig.Apply(key, rig.Build(key, generation: 1, layer));

            rig.ResetDraws();
            rig.Renderer.Draw(Focus(100f));

            Assert.Equal(1, rig.MergedDrawCount);
            Assert.Equal(0.5f, rig.LastMergedDissolve, 5);
            Assert.False(rig.LastMergedInvertShadow);
        }

        static Vector3 Focus(float clusterDistance) => new(32f + clusterDistance, 0f, 32f);

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
            public int DrawCount => _backend.DrawCount;
            public int MergedDrawCount => _backend.MergedDrawCount;
            public float LastMergedDissolve => _backend.LastMergedDissolve;
            public bool LastMergedInvertShadow => _backend.LastMergedInvertShadow;

            public static PropLayer ExitLayer(float drawRadius, float fadeWidth,
                                              float hlodDistance, float hlodWidth) =>
                PropLayer.PlacementLayer(
                        new[] { Placement },
                        new Dictionary<string, MeshHandle> { ["oak"] = new MeshHandle(90) },
                        drawRadius,
                        fadeWidth)
                    .WithHlod(
                        new Dictionary<string, GltfMesh> { ["oak"] = MeshPrimitives.Box(1f) },
                        hlodDistance,
                        weldCell: 0f,
                        crossfadeWidth: hlodWidth);

            public PropClusterCpuBuild Build(PropClusterKey key, long generation)
                => Build(key, generation, Placement);

            public PropClusterCpuBuild Build(PropClusterKey key, long generation, PropPlacement placement)
                => Build(key, generation, Layer, placement);

            public PropClusterCpuBuild Build(PropClusterKey key, long generation, PropLayer layer)
                => Build(key, generation, layer, Placement);

            PropClusterCpuBuild Build(PropClusterKey key, long generation, PropLayer layer,
                                      PropPlacement placement)
            {
                var request = new PropClusterBuildRequest(
                    key,
                    generation,
                    new RectArea(key.X * 64f, key.Z * 64f, (key.X + 1) * 64f, (key.Z + 1) * 64f),
                    layer,
                    new[] { placement });
                return Renderer.BuildCpu(request);
            }

            public void ResetDraws() => _backend.ResetDraws();

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
            public int DrawCount;
            public int MergedDrawCount;
            public float LastMergedDissolve;
            public bool LastMergedInvertShadow;

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
                if (placements.Count > 0)
                {
                    LastDrawX = placements[0].X;
                    DrawCount++;
                }
            }

            public void DrawMerged(MeshHandle handle, PropLayer layer, float dissolve, bool invertShadowDissolve)
            {
                DrawCount++;
                MergedDrawCount++;
                LastMergedDissolve = dissolve;
                LastMergedInvertShadow = invertShadowDissolve;
            }

            public void ResetDraws()
            {
                DrawCount = 0;
                MergedDrawCount = 0;
                LastMergedDissolve = 0f;
                LastMergedInvertShadow = false;
            }
        }

        static TerrainField Flat(float height) => new(new TerrainConfig
        {
            WaterLevel = 0f,
            Biomes = new[]
            {
                new BiomeBand
                {
                    Start = float.NegativeInfinity,
                    End = float.PositiveInfinity,
                    Biome = BiomeId.Meadow,
                    BaseHeight = height,
                    HillAmplitude = 0f,
                },
            },
        });
    }
}
