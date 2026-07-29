using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Issue #393: the HLOD cluster merge must be built only when the apply is going to consume it. A tier
    /// re-LOD and a ring change both keep the mesh already on the GPU, so merging for them was multi-megabyte
    /// large-object work thrown away on every tier flip a running player crosses.
    /// <para>The gate rule is tested twice over. <see cref="HlodBuildGate"/> is exercised directly (pure, headless),
    /// and the sink's own decision is exercised through <c>BuildCpu</c> with the gate primed to the applied state a
    /// re-LOD would see, which is why <c>Scene3DChunkSink.HlodGate</c> is internal: it makes the whole decision
    /// headless instead of needing a GPU device to run <c>Apply</c> through. The GPU-gated tests at the bottom close
    /// the loop end to end on the built-versus-consumed counters.</para></summary>
    public sealed class HlodMergeGateTests
    {
        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();

        // Frame boundaries to keep driving before giving up on the retired-resource pool emptying. It is a BOUND,
        // not a delay: on a device with GPU-completion fences the pool frees a batch on the first boundary whose
        // fence has signaled, which is an event and not a frame count, so a fixed number of Begin calls proves
        // nothing (four of them in a tight loop can all land inside one command-buffer round trip).
        const int RetiredDrainBoundaryLimit = 300;

        static TerrainField Flat(float height, float waterLevel = 0f) => new(new TerrainConfig
        {
            Seed = 1,
            WaterLevel = waterLevel,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
            },
        });

        static ScatterConfig OneKind(string id, int seed, float cell) => new()
        {
            Seed = seed,
            CellSize = cell,
            Jitter = 0.5f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[]
            {
                new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind(id, 1f) } },
            },
        };

        static IReadOnlyDictionary<string, GltfMesh> KitSource(params string[] ids)
        {
            var d = new Dictionary<string, GltfMesh>();
            foreach (string id in ids) d[id] = MeshPrimitives.Sphere(radius: 1f, rings: 8, segments: 10);
            return d;
        }

        static Scene3DChunkSink HlodSink(Scene3D? scene, TerrainField field) =>
            new(scene!, field,
                new[] { PropLayer.ScatterLayer(OneKind("pine_a", seed: 3, cell: 6f), NoMeshes(), 90f)
                                 .WithHlod(KitSource("pine_a"), hlodDistance: 120f, weldCell: 2f) },
                chunkSize: 60f);

        // --- The gate rule, pure ---------------------------------------------------------------------------------

        [Fact]
        public void NeedsMerge_IsTrue_ForAChunkWithNoAppliedBuild()
        {
            var gate = new HlodBuildGate();
            Assert.True(gate.NeedsMerge(new ChunkCoord(2, -3), lod: 1, ChunkRing.Gameplay));
        }

        [Fact]
        public void NeedsMerge_IsFalse_ForATierChangeOrARingChange()
        {
            var gate = new HlodBuildGate();
            var coord = new ChunkCoord(0, 0);
            gate.MarkApplied(coord, lod: 0, ChunkRing.Gameplay);

            Assert.False(gate.NeedsMerge(coord, lod: 1, ChunkRing.Gameplay));   // tier flip
            Assert.False(gate.NeedsMerge(coord, lod: 0, ChunkRing.Decor));      // ring change
            Assert.False(gate.NeedsMerge(coord, lod: 2, ChunkRing.Decor));      // both at once
        }

        [Fact]
        public void NeedsMerge_IsTrue_ForARebuildInPlace()
        {
            var gate = new HlodBuildGate();
            var coord = new ChunkCoord(0, 0);
            gate.MarkApplied(coord, lod: 2, ChunkRing.Decor);

            // Same tier, same ring: the only shape a rebuild-in-place takes (TerrainStreamer.Invalidate).
            Assert.True(gate.NeedsMerge(coord, lod: 2, ChunkRing.Decor));
        }

        [Fact]
        public void MarkApplied_TracksTheNewTier_SoARebuildInPlaceAtItIsStillRecognized()
        {
            // The applied state is recorded on EVERY apply, including the tier flips that merge nothing. Without
            // that, a rebuild-in-place after a tier flip would compare against the tier the chunk was FIRST loaded
            // at and skip the merge, leaving the edited cluster stale on screen.
            var gate = new HlodBuildGate();
            var coord = new ChunkCoord(0, 0);
            gate.MarkApplied(coord, lod: 0, ChunkRing.Gameplay);
            gate.MarkApplied(coord, lod: 1, ChunkRing.Gameplay);

            Assert.True(gate.NeedsMerge(coord, lod: 1, ChunkRing.Gameplay));
            Assert.False(gate.NeedsMerge(coord, lod: 0, ChunkRing.Gameplay));
        }

        [Fact]
        public void Forget_AndClear_PutAChunkBackToNeedingAMerge()
        {
            var gate = new HlodBuildGate();
            var a = new ChunkCoord(0, 0);
            var b = new ChunkCoord(1, 0);
            gate.MarkApplied(a, lod: 0, ChunkRing.Gameplay);
            gate.MarkApplied(b, lod: 0, ChunkRing.Gameplay);

            gate.Forget(a);
            Assert.True(gate.NeedsMerge(a, lod: 1, ChunkRing.Gameplay));    // unloaded: a reload merges again
            Assert.False(gate.NeedsMerge(b, lod: 1, ChunkRing.Gameplay));   // untouched neighbour keeps its state

            gate.Clear();
            Assert.True(gate.NeedsMerge(b, lod: 1, ChunkRing.Gameplay));
        }

        [Fact]
        public void Gate_IsOnlyAllocated_WhenALayerBakesHlod()
        {
            TerrainField field = Flat(5f);
            var plain = new Scene3DChunkSink(scene: null!, field, OneKind("pine_a", seed: 3, cell: 6f),
                NoMeshes(), chunkSize: 60f, propDrawRadius: 90f);

            Assert.Null(plain.HlodGate);                 // no HLOD layer: the whole path stays inert
            Assert.NotNull(HlodSink(null, field).HlodGate);
        }

        // --- What BuildCpu actually does with it (headless: BuildCpu never touches the GPU) ------------------------

        [Fact]
        public void BuildCpu_ForAFreshLoad_MergesTheCluster()
        {
            Scene3DChunkSink sink = HlodSink(null, Flat(5f));

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(0, 0), lod: 0);

            Assert.NotNull(cpu.HlodMeshes);
            Assert.True(cpu.HlodMeshes![0]!.TriangleCount > 0);   // not vacuous: this chunk really does cluster
            Assert.Equal(1, sink.MergeStats.Built);
        }

        [Fact]
        public void BuildCpu_ForATierReLod_MergesNothing()
        {
            Scene3DChunkSink sink = HlodSink(null, Flat(5f));
            var coord = new ChunkCoord(0, 0);
            sink.HlodGate!.MarkApplied(coord, lod: 0, ChunkRing.Gameplay);

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 1, ChunkRing.Gameplay);

            Assert.Null(cpu.HlodMeshes);                       // no merge in the payload: Apply keeps what it has
            Assert.Equal(0, sink.MergeStats.Built);
            Assert.NotEmpty(cpu.LayerProps[0]);                // the gameplay chunk's own props are unaffected
        }

        [Fact]
        public void BuildCpu_ForARingChange_MergesNothing()
        {
            Scene3DChunkSink sink = HlodSink(null, Flat(5f));
            var coord = new ChunkCoord(0, 0);
            sink.HlodGate!.MarkApplied(coord, lod: 0, ChunkRing.Gameplay);

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 0, ChunkRing.Decor);

            Assert.Null(cpu.HlodMeshes);
            Assert.Equal(0, sink.MergeStats.Built);
        }

        [Fact]
        public void BuildCpu_ForARebuildInPlace_MergesTheClusterAgain()
        {
            Scene3DChunkSink sink = HlodSink(null, Flat(5f));
            var coord = new ChunkCoord(0, 0);
            sink.HlodGate!.MarkApplied(coord, lod: 1, ChunkRing.Gameplay);

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 1, ChunkRing.Gameplay);

            Assert.NotNull(cpu.HlodMeshes);                    // an editor edit / a source arrival must see fresh geometry
            Assert.Equal(1, sink.MergeStats.Built);
        }

        // A placement source that counts how many times the sink asked it for a chunk's props.
        sealed class CountingSource : IPlacementSource
        {
            public int Queries;
            public void PlacementsIn(RectArea area, List<PropPlacement> into)
            {
                Queries++;
                into.Add(new PropPlacement("pine_a", area.MinX + 1f, 5f, area.MinZ + 1f, 1f, 0f, 0));
            }
        }

        [Fact]
        public void BuildCpu_ForADecorReLod_DoesNotEvenQueryPlacements()
        {
            // The decor ring carries no props of its own, so its only reason to ask for placements is the merge. Once
            // the merge is gated the query has to go with it, or the saving is only half taken.
            var source = new CountingSource();
            var sink = new Scene3DChunkSink(scene: null!, Flat(5f),
                new[] { PropLayer.PlacementLayer(source, NoMeshes(), 90f)
                                 .WithHlod(KitSource("pine_a"), hlodDistance: 120f, weldCell: 2f) },
                chunkSize: 60f);
            var coord = new ChunkCoord(0, 0);

            sink.BuildCpu(coord, lod: 2, ChunkRing.Decor);       // fresh load: merges, so it must query
            Assert.Equal(1, source.Queries);

            sink.HlodGate!.MarkApplied(coord, lod: 2, ChunkRing.Decor);
            sink.BuildCpu(coord, lod: 3, ChunkRing.Decor);       // tier flip on the decor ring: nothing to build
            Assert.Equal(1, source.Queries);
        }

        // --- End to end through a real apply: the built/consumed counters must balance ------------------------------

        [GpuFact]
        public void Load_ThenTierReLod_DiscardsNoMergeWork() => WithScene(scene =>
        {
            Scene3DChunkSink sink = HlodSink(scene, Flat(5f));
            var coord = new ChunkCoord(0, 0);

            object handle = sink.Load(coord, lod: 0);
            var load = (Scene3DChunkSink.ChunkLoad)handle;
            MeshHandle uploaded = load.HlodMeshHandles![0]!.Value;
            HlodMergeStats afterLoad = sink.MergeStats;
            Assert.Equal(1, afterLoad.Built);
            Assert.Equal(1, afterLoad.Uploaded);
            Assert.True(afterLoad.BuiltBytes > 0);              // not vacuous: a real cluster was merged

            sink.ReLod(coord, handle, lod: 1);
            sink.ReLod(coord, handle, lod: 2, ring: ChunkRing.Decor);
            sink.ReLod(coord, handle, lod: 0, ring: ChunkRing.Gameplay);

            HlodMergeStats after = sink.MergeStats;
            Assert.Equal(1, after.Built);                        // three re-LODs, not one extra merge
            Assert.Equal(0, after.Discarded);
            Assert.Equal(0, after.DiscardedBytes);
            Assert.Equal(afterLoad.BuiltBytes, after.BuiltBytes);
            MeshHandle still = load.HlodMeshHandles![0]!.Value;
            Assert.Equal(uploaded.Index, still.Index);           // and the uploaded mesh was never churned
            Assert.Equal(uploaded.Generation, still.Generation);
        });

        [GpuFact]
        public void RebuildInPlace_StillMergesAndConsumes() => WithScene(scene =>
        {
            Scene3DChunkSink sink = HlodSink(scene, Flat(5f));
            var coord = new ChunkCoord(0, 0);

            object handle = sink.Load(coord, lod: 0);
            sink.ReLod(coord, handle, lod: 0);                   // same tier + ring: the Invalidate shape

            HlodMergeStats stats = sink.MergeStats;
            Assert.Equal(2, stats.Built);
            Assert.Equal(2, stats.Uploaded);
            Assert.Equal(0, stats.DiscardedBytes);
        });

        [GpuFact]
        public void Unload_ThenReload_MergesAgain() => WithScene(scene =>
        {
            Scene3DChunkSink sink = HlodSink(scene, Flat(5f));
            var coord = new ChunkCoord(0, 0);

            object handle = sink.Load(coord, lod: 0);
            sink.Unload(coord, handle);
            var reloaded = (Scene3DChunkSink.ChunkLoad)sink.Load(coord, lod: 0);

            Assert.Equal(2, sink.MergeStats.Built);              // the merged mesh went with the unload
            Assert.Equal(0, sink.MergeStats.Discarded);
            Assert.NotNull(reloaded.HlodMeshHandles![0]);        // and the reload really does have one again
        });

        [GpuFact]
        public void RetiredResourceCount_TracksThePendingUnloadedMeshes() => WithScene(scene =>
        {
            Scene3DChunkSink sink = HlodSink(scene, Flat(5f));
            var coord = new ChunkCoord(0, 0);
            Assert.Equal(0, scene.RetiredResourceCount);

            object handle = sink.Load(coord, lod: 0);
            sink.Unload(coord, handle);
            Assert.True(scene.RetiredResourceCount > 0);         // freed mid-life, held until the GPU is done with it

            for (int i = 0; i < RetiredDrainBoundaryLimit && scene.RetiredResourceCount > 0; i++)
            {
                scene.Begin();
                if (scene.RetiredResourceCount > 0) System.Threading.Thread.Sleep(1);
            }
            Assert.Equal(0, scene.RetiredResourceCount);         // and released once it provably is
        });
    }
}
