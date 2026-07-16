using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Physics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the multi-layer chunk sink + the PropLayer tagged struct (no GPU - all assertions
    /// go through the internal ScatterLayersFor / ScatterFor accessors and the PropLayer factories), plus a handful
    /// of GPU-gated tests (Apply always uploads through a real Scene3D) for the re-LOD prop-adoption fix. Those
    /// mirror the WithScene helper in Scene3DChunkSinkDisposeGpuTests.cs and are skipped unless KE_GPU_TESTS is set.</summary>
    public class Scene3DChunkSinkTests
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

        // Minimal fake IPhysicsWorld: records AddStatic / RemoveStatic calls (mirrors ChunkStaticsTests' fake, kept
        // local here since that one is private to its own test class).
        sealed class FakePhysicsWorld : IPhysicsWorld
        {
            int _next = 1;
            public readonly List<(PhysicsShape Shape, Pose Pose)> Added = new();
            public readonly List<StaticHandle> Removed = new();

            public StaticHandle AddStatic(PhysicsShape shape, Pose pose, PhysicsMaterial? material = null)
            {
                Added.Add((shape, pose));
                return new StaticHandle(_next++);
            }

            public void RemoveStatic(StaticHandle handle) => Removed.Add(handle);

            public DynamicBodyHandle AddDynamic(PhysicsShape shape, Pose pose, DynamicBodyDescription body, PhysicsMaterial? material = null)
                => throw new NotSupportedException();
            public void RemoveDynamic(DynamicBodyHandle handle) => throw new NotSupportedException();
            public Pose GetDynamicPose(DynamicBodyHandle handle) => throw new NotSupportedException();
            public void GetDynamicVelocity(DynamicBodyHandle handle, out Vector3 linear, out Vector3 angular)
                => throw new NotSupportedException();
            public void SetDynamicVelocity(DynamicBodyHandle handle, Vector3 linear, Vector3 angular)
                => throw new NotSupportedException();
            public bool IsAwake(DynamicBodyHandle handle) => throw new NotSupportedException();
            public ConstraintHandle AddConstraint(in ConstraintDescription description) => throw new NotSupportedException();
            public void RemoveConstraint(ConstraintHandle handle) => throw new NotSupportedException();
            public void SetConstraintTarget(ConstraintHandle handle, float target) => throw new NotSupportedException();
            public void Step(float dt) { }
            public bool Raycast(Vector3 o, Vector3 d, float max, out RayHit hit, QueryFilter f = default)
                => throw new NotSupportedException();
            public bool SweepCapsule(CapsuleShape c, Pose p, Vector3 d, float max, out SweepHit hit, QueryFilter f = default)
                => throw new NotSupportedException();
            public bool ComputePenetration(CapsuleShape c, Pose p, out Vector3 mtv)
                => throw new NotSupportedException();
            public void Dispose() { }
        }

        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();

        static ScatterConfig OneKind(string id, int seed, float cell) =>
            new ScatterConfig
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

        [Fact]
        public void ScatterLayer_factory_sets_a_scatter_layer()
        {
            ScatterConfig cfg = OneKind("pine_a", 1, 8f);
            var meshes = NoMeshes();
            PropLayer layer = PropLayer.ScatterLayer(cfg, meshes, 90f);

            Assert.False(layer.IsCompanion);
            Assert.Same(cfg, layer.Scatter);
            Assert.Null(layer.Companions);
            Assert.Equal(-1, layer.HostLayerIndex);
            Assert.Equal(90f, layer.DrawRadius);
            Assert.Same(meshes, layer.Meshes);
        }

        [Fact]
        public void CompanionLayer_factory_sets_a_companion_layer()
        {
            var comp = new CompanionConfig { HostKinds = new[] { "pine_a" }, Kinds = new[] { new PropKind("bush", 1f) } };
            var meshes = NoMeshes();
            PropLayer layer = PropLayer.CompanionLayer(0, comp, meshes, 40f);

            Assert.True(layer.IsCompanion);
            Assert.Same(comp, layer.Companions);
            Assert.Null(layer.Scatter);
            Assert.Equal(0, layer.HostLayerIndex);
            Assert.Equal(40f, layer.DrawRadius);
            Assert.Same(meshes, layer.Meshes);
        }

        static void AssertSamePlacements(IReadOnlyList<PropPlacement> expected, IReadOnlyList<PropPlacement> got)
        {
            Assert.Equal(expected.Count, got.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Id, got[i].Id);
                Assert.Equal(expected[i].X, got[i].X, 4);
                Assert.Equal(expected[i].Z, got[i].Z, 4);
                Assert.Equal(expected[i].Y, got[i].Y, 4);
                Assert.Equal(expected[i].Scale, got[i].Scale, 4);
                Assert.Equal(expected[i].Yaw, got[i].Yaw, 4);
                Assert.Equal(expected[i].Variant, got[i].Variant);
            }
        }

        static string SetKey(PropPlacement p) => $"{p.X:F3},{p.Z:F3},{p.Y:F3},{p.Id},{p.Variant}";

        [Fact]
        public void Each_scatter_layer_matches_PropScatter_for_the_chunk_area()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig trees = ScatterConfig.ForestRing();
            ScatterConfig cover = OneKind("grass", seed: 42, cell: 2f);
            cover.ClearingRadius = 26f;
            cover.MaxHeight = 6f;
            float size = 60f;

            var sink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.ScatterLayer(trees, NoMeshes(), 90f),
                PropLayer.ScatterLayer(cover, NoMeshes(), 40f),
            }, chunkSize: size);

            var coord = new ChunkCoord(-2, -2);
            var got = sink.ScatterLayersFor(coord);
            var area = ChunkGrid.AreaOf(coord, size);

            Assert.Equal(2, got.Length);
            Assert.NotEmpty(got[0]);   // not a vacuous comparison: this meadow chunk has trees and grass
            Assert.NotEmpty(got[1]);
            AssertSamePlacements(PropScatter.Generate(field, trees, area), got[0]);
            AssertSamePlacements(PropScatter.Generate(field, cover, area), got[1]);
        }

        [Fact]
        public void Single_layer_ctor_is_back_compatible()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig trees = ScatterConfig.ForestRing();
            float size = 60f;

            var legacy = new Scene3DChunkSink(scene: null!, field, trees, NoMeshes(), chunkSize: size, propDrawRadius: 90f);
            var multi = new Scene3DChunkSink(scene: null!, field, new[] { PropLayer.ScatterLayer(trees, NoMeshes(), 90f) }, chunkSize: size);

            var coord = new ChunkCoord(-2, -2);
            AssertSamePlacements(PropScatter.Generate(field, trees, ChunkGrid.AreaOf(coord, size)), legacy.ScatterFor(coord));
            AssertSamePlacements(legacy.ScatterFor(coord), multi.ScatterFor(coord));
        }

        [Fact]
        public void Companion_layer_emits_each_host_companions_exactly_once_across_chunks()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            ScatterConfig trees = ScatterConfig.ForestRing();
            var comp = new CompanionConfig
            {
                Seed = 7,
                HostKinds = new[] { "pine_a", "pine_b", "pine_c", "oak_a", "oak_b" },
                Kinds = new[] { new PropKind("bush", 1f) },
                CountMin = 2,
                CountMax = 3,
                RadiusMin = 0.6f,
                RadiusMax = 1.6f,
                MaxHeight = 6f,
            };
            float size = 60f;

            var sink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.ScatterLayer(trees, NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
            }, chunkSize: size);

            // Companions gathered per chunk over a 4x4 block (each host attached to its own chunk).
            var perChunk = new List<PropPlacement>();
            for (int cx = -2; cx <= 1; cx++)
                for (int cz = -2; cz <= 1; cz++)
                    perChunk.AddRange(sink.ScatterLayersFor(new ChunkCoord(cx, cz))[1]);

            // Reference: derive companions once from all hosts over the same world block [-120,120)x[-120,120).
            var hostsWhole = PropScatter.Generate(field, trees, new RectArea(-120, -120, 120, 120));
            var compWhole = PropScatter.GenerateCompanions(field, hostsWhole, comp);

            Assert.NotEmpty(compWhole);
            Assert.Equal(compWhole.Count, perChunk.Count);   // exactly once: no double-emit at seams, none missing
            Assert.Equal(compWhole.Select(SetKey).OrderBy(s => s).ToList(),
                         perChunk.Select(SetKey).OrderBy(s => s).ToList());
        }

        [Fact]
        public void Ctor_rejects_empty_layers_and_bad_companion_host()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var comp = new CompanionConfig { HostKinds = new[] { "pine_a" }, Kinds = new[] { new PropKind("bush", 1f) } };

            Assert.Throws<ArgumentException>(() =>
                new Scene3DChunkSink(scene: null!, field, Array.Empty<PropLayer>(), chunkSize: 60f));

            // Companion host index out of range.
            Assert.Throws<ArgumentException>(() =>
                new Scene3DChunkSink(scene: null!, field,
                    new[] { PropLayer.CompanionLayer(5, comp, NoMeshes(), 40f) }, chunkSize: 60f));

            // Companion host points at another companion (must be a scatter layer).
            Assert.Throws<ArgumentException>(() =>
                new Scene3DChunkSink(scene: null!, field, new[]
                {
                    PropLayer.CompanionLayer(1, comp, NoMeshes(), 40f),
                    PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
                }, chunkSize: 60f));
        }

        static TerrainField Flat(float height, float waterLevel = 0f) => new TerrainField(new TerrainConfig
        {
            GentleAmplitude = 0f,
            WaterLevel = waterLevel,
            Biomes = new[]
            {
                new BiomeBand { Start = float.NegativeInfinity, End = float.PositiveInfinity, Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f },
            },
        });

        [Fact]
        public void UpdateField_swaps_field_for_subsequent_BuildCpu_calls()
        {
            var fieldA = Flat(0f);
            var fieldB = Flat(5f);
            var sink = new Scene3DChunkSink(scene: null!, fieldA, new ScatterConfig(),
                propMeshes: NoMeshes(), chunkSize: 60f, propDrawRadius: 90f);
            var coord = new ChunkCoord(0, 0);

            var before = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 1);
            sink.UpdateField(fieldB);
            var after = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 1);

            for (int i = 0; i < before.Mesh.SurfaceVertexCount; i++)
                Assert.Equal(0f, before.Mesh.Mesh.Vertices[i].Position.Y, 3);
            for (int i = 0; i < after.Mesh.SurfaceVertexCount; i++)
                Assert.Equal(5f, after.Mesh.Mesh.Vertices[i].Position.Y, 3);
        }

        [Fact]
        public void UpdateField_null_throws()
        {
            var field = Flat(0f);
            var sink = new Scene3DChunkSink(scene: null!, field, new ScatterConfig(),
                propMeshes: NoMeshes(), chunkSize: 60f, propDrawRadius: 90f);

            Assert.Throws<ArgumentNullException>(() => sink.UpdateField(null!));
        }

        // --- Re-LOD prop adoption (stale-trees regression): a re-LOD Apply must adopt the fresh scatter BuildCpu ---
        // --- already computed, not keep the handle's old LayerProps array. GPU-gated because Apply always uploads
        // --- through a real Scene3D (UploadMesh has no GPU-free path). ------------------------------------------

        [GpuFact]
        public void ReLod_AfterFieldSwap_RegeneratesLayerProps_WhenCarvedBelowWater() => WithScene(scene =>
        {
            TerrainField fieldA = Flat(5f);                    // WaterLevel 0: candidates at y=5 are kept
            TerrainField fieldB = Flat(5f, waterLevel: 10f);    // carved below water: every candidate excluded
            ScatterConfig scatter = OneKind("pine_a", seed: 3, cell: 6f);
            var sink = new Scene3DChunkSink(scene, fieldA, scatter, NoMeshes(), chunkSize: 60f, propDrawRadius: 90f);
            var coord = new ChunkCoord(0, 0);

            object handle = sink.Load(coord, lod: 0);
            var load = (Scene3DChunkSink.ChunkLoad)handle;
            Assert.True(load.LayerProps[0].Count > 0);   // not vacuous: the pre-carve chunk has trees

            sink.UpdateField(fieldB);
            sink.ReLod(coord, handle, lod: 0);

            Assert.Empty(load.LayerProps[0]);              // regenerated: the carved chunk has no props left standing
        });

        [GpuFact]
        public void ReLod_AfterFieldSwap_AddsLayerProps_WhenRaisedAboveWater() => WithScene(scene =>
        {
            TerrainField fieldA = Flat(5f, waterLevel: 10f);   // carved: no candidate clears the water
            TerrainField fieldB = Flat(5f);                    // WaterLevel 0: candidates are kept
            ScatterConfig scatter = OneKind("pine_a", seed: 3, cell: 6f);
            var sink = new Scene3DChunkSink(scene, fieldA, scatter, NoMeshes(), chunkSize: 60f, propDrawRadius: 90f);
            var coord = new ChunkCoord(0, 0);

            object handle = sink.Load(coord, lod: 0);
            var load = (Scene3DChunkSink.ChunkLoad)handle;
            Assert.Empty(load.LayerProps[0]);

            sink.UpdateField(fieldB);
            sink.ReLod(coord, handle, lod: 0);

            Assert.True(load.LayerProps[0].Count > 0);    // regenerated: props now populate the drained chunk
        });

        [GpuFact]
        public void ReLod_WithNoFieldChange_KeepsIdenticalLayerProps() => WithScene(scene =>
        {
            TerrainField field = Flat(5f);
            ScatterConfig scatter = OneKind("pine_a", seed: 3, cell: 6f);
            var sink = new Scene3DChunkSink(scene, field, scatter, NoMeshes(), chunkSize: 60f, propDrawRadius: 90f);
            var coord = new ChunkCoord(0, 0);

            object handle = sink.Load(coord, lod: 0);
            var load = (Scene3DChunkSink.ChunkLoad)handle;
            var before = load.LayerProps[0].ToList();   // snapshot: the array reference is replaced by ReLod
            Assert.NotEmpty(before);

            sink.ReLod(coord, handle, lod: 1);           // LOD tier change only, field untouched

            AssertSamePlacements(before, load.LayerProps[0]);   // determinism pin: identical content, not just count
        });

        [GpuFact]
        public void ReLod_WithPhysicsWired_RefreshesPropStatics() => WithScene(scene =>
        {
            TerrainField fieldA = Flat(5f);
            TerrainField fieldB = Flat(5f, waterLevel: 10f);   // carve everything below water
            ScatterConfig scatter = OneKind("pine_a", seed: 3, cell: 6f);
            var shapes = new Dictionary<string, PhysicsShape> { ["pine_a"] = new BoxShape(new Vector3(0.5f, 1f, 0.5f)) };
            var world = new FakePhysicsWorld();
            var sink = new Scene3DChunkSink(scene, fieldA, scatter, NoMeshes(), chunkSize: 60f, propDrawRadius: 90f,
                physics: world, collisionShapes: shapes);
            var coord = new ChunkCoord(0, 0);

            object handle = sink.Load(coord, lod: 0);
            var load = (Scene3DChunkSink.ChunkLoad)handle;
            int staticsBefore = load.Statics.Count;
            Assert.True(staticsBefore > 0);
            Assert.Equal(staticsBefore, world.Added.Count);

            sink.UpdateField(fieldB);
            sink.ReLod(coord, handle, lod: 0);

            Assert.Empty(load.Statics);                        // carved: no props left, so no live statics remain
            Assert.Equal(staticsBefore, world.Removed.Count);  // every stale static was torn down, none leaked
        });
    }
}
