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
    /// <summary>Covers <see cref="PropLayer.PlacementLayer(IReadOnlyList{PropPlacement}, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float, bool)"/>
    /// and its multi-part overload (issue #286): the frozen, author-supplied placement kind, its collider opt-out,
    /// and that <see cref="PropLayer.WithHlod"/> carries both through unchanged. Then the sink wiring:
    /// <see cref="PlacementBuckets"/>, the shared per-chunk seam in <c>ScatterLayersFor</c> (a placement layer
    /// serves a bucket where a scatter layer generates), the collider gating in <c>LayerRegistersColliders</c>, and
    /// the downstream parity that seam buys for free: a placement layer's <c>BuildCpu</c> HLOD bake and its
    /// <c>PropRenderer.Queue</c> fade/LOD selection match the scatter layer that produced its placements, chunk for
    /// chunk. Headless apart from one GPU-gated end-to-end collider test; pixel-presence GPU coverage of a
    /// placement layer drawing is in the companion <c>PlacementLayerGpuTests.cs</c>.</summary>
    public class PlacementLayerTests
    {
        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();

        static IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> NoPartMeshes() =>
            new Dictionary<string, IReadOnlyList<MeshHandle>>();

        static IReadOnlyList<PropPlacement> OnePlacement() =>
            new[] { new PropPlacement("tree", 1f, 0f, 2f, 1f, 0f, 0) };

        [Fact]
        public void PlacementLayer_NullPlacements_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(null!, NoMeshes(), 90f));
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(null!, NoPartMeshes(), 90f));
        }

        [Fact]
        public void PlacementLayer_NullMeshes_Throws()
        {
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(placements, (IReadOnlyDictionary<string, MeshHandle>)null!, 90f));
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(placements, (IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>)null!, 90f));
        }

        [Fact]
        public void PlacementLayer_StoresKnobs()
        {
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            IReadOnlyDictionary<string, MeshHandle> meshes = NoMeshes();
            IReadOnlyDictionary<string, MeshHandle> lodMeshes = NoMeshes();

            PropLayer layer = PropLayer.PlacementLayer(placements, meshes, 120f, 15f, lodMeshes, 60f);

            Assert.Same(placements, layer.Placements);
            Assert.Equal(120f, layer.DrawRadius);
            Assert.Equal(15f, layer.FadeBandWidth);
            Assert.Same(lodMeshes, layer.LodMeshes);
            Assert.Equal(60f, layer.LodDistance);
            Assert.True(layer.IsPlacement);
            Assert.False(layer.IsCompanion);
            Assert.Null(layer.Scatter);
            Assert.Null(layer.Companions);
            Assert.True(layer.RegisterColliders);
        }

        [Fact]
        public void PlacementLayer_CollidersOptOut()
        {
            PropLayer layer = PropLayer.PlacementLayer(OnePlacement(), NoMeshes(), 90f, colliders: false);
            Assert.False(layer.RegisterColliders);
        }

        [Fact]
        public void PlacementLayer_MultiPart_StoresPartMeshes()
        {
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> partMeshes = NoPartMeshes();
            IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> lodPartMeshes = NoPartMeshes();

            PropLayer layer = PropLayer.PlacementLayer(OnePlacement(), partMeshes, 90f, 10f, lodPartMeshes, 40f);

            Assert.Same(partMeshes, layer.PartMeshes);
            Assert.Empty(layer.Meshes);
            Assert.Same(lodPartMeshes, layer.LodPartMeshes);
            Assert.True(layer.IsPlacement);
        }

        [Fact]
        public void PlacementLayer_WithHlod_PreservesPlacementsAndColliders()
        {
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            PropLayer layer = PropLayer.PlacementLayer(placements, NoMeshes(), 90f, 15f, NoMeshes(), 60f,
                colliders: false);

            var source = new Dictionary<string, GltfMesh>();
            PropLayer hlod = layer.WithHlod(source, hlodDistance: 120f, weldCell: 2f);

            Assert.Same(placements, hlod.Placements);
            Assert.False(hlod.RegisterColliders);
            Assert.True(hlod.HasHlod);
            Assert.Equal(15f, hlod.FadeBandWidth);
            Assert.Equal(60f, hlod.LodDistance);
        }

        [Fact]
        public void ScatterLayer_RegisterCollidersDefaultsTrue()
        {
            PropLayer layer = PropLayer.ScatterLayer(new ScatterConfig(), NoMeshes(), 90f);
            Assert.True(layer.RegisterColliders);
            Assert.Null(layer.Placements);
            Assert.False(layer.IsPlacement);
        }

        // --- Sink wiring: bucketing, the shared per-chunk seam, and collider gating (issue #286) ------------------

        // Minimal fake IPhysicsWorld: records AddStatic / RemoveStatic calls. Mirrors the local fakes in
        // Scene3DChunkSinkTests.cs and ChunkStaticsTests.cs, each private to its own test class.
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

        // Mirrors the WithScene helper in Scene3DChunkSinkTests.cs (Apply always uploads through a real Scene3D).
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

        // Flat meadow everywhere, so every scatter candidate clears the biome / water / height filters. Mirrors the
        // Flat helper in Scene3DChunkSinkTests.cs.
        static TerrainField Flat(float height) => new TerrainField(new TerrainConfig
        {
            GentleAmplitude = 0f,
            WaterLevel = 0f,
            Biomes = new[]
            {
                new BiomeBand
                {
                    Start = float.NegativeInfinity, End = float.PositiveInfinity,
                    Biome = BiomeId.Meadow, BaseHeight = height, HillAmplitude = 0f,
                },
            },
        });

        // A single-kind scatter with NO jitter, which is what lets a scatter layer and a placement layer fed its
        // output be compared per chunk. PropScatter assigns a cell to a chunk by the cell CENTRE against the chunk's
        // half-open area, while a placement layer buckets by the placement's own position through ChunkGrid.CoordOf.
        // Zero jitter makes the position the cell centre, so the two rules coincide. With jitter on, a cell centred
        // on a chunk edge could land either side and the two would legitimately disagree.
        static ScatterConfig NoJitterKind(string id, int seed, float cell) => new ScatterConfig
        {
            Seed = seed,
            CellSize = cell,
            Jitter = 0f,
            ClearingRadius = 0f,
            MaxHeight = null,
            Biomes = new[]
            {
                new BiomeScatterRule { Biome = BiomeId.Meadow, Density = 1f, Kinds = new[] { new PropKind(id, 1f) } },
            },
        };

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

        const float ZoneChunk = 60f;     // chunk size for the 3x3 zone fixtures below
        const float ZoneCell = 15f;      // scatter cell size, an exact divisor of the chunk size

        // The whole-zone placement list a frozen zone would ship: one Generate call over the 3x3 chunk block
        // (-1,-1)..(1,1), which is exactly what a placement layer is handed instead of a runtime config.
        static IReadOnlyList<PropPlacement> WholeZone(TerrainField field, ScatterConfig cfg) =>
            PropScatter.Generate(field, cfg, new RectArea(-ZoneChunk, -ZoneChunk, 2f * ZoneChunk, 2f * ZoneChunk));

        static IEnumerable<ChunkCoord> ZoneCoords()
        {
            for (int cx = -1; cx <= 1; cx++)
                for (int cz = -1; cz <= 1; cz++)
                    yield return new ChunkCoord(cx, cz);
        }

        [Fact]
        public void Buckets_EveryPlacementLandsInExactlyOneChunk()
        {
            const float size = 10f;
            // Spans negative and positive coords, and includes exact-edge positions: floor semantics put x = k * size
            // in chunk k, and a negative coordinate floors downward rather than toward zero.
            var placements = new[]
            {
                new PropPlacement("a", 0f, 0f, 0f, 1f, 0f, 0),          // exact origin edge -> (0, 0)
                new PropPlacement("b", 10f, 0f, 0f, 1f, 0f, 0),         // exact edge x = size -> (1, 0)
                new PropPlacement("c", -1f, 0f, -1f, 1f, 0f, 0),        // negative -> (-1, -1)
                new PropPlacement("d", -10f, 0f, -10f, 1f, 0f, 0),      // exact negative edge -> (-1, -1)
                new PropPlacement("e", 9.9f, 0f, 0.5f, 1f, 0f, 0),      // just inside (0, 0)
                new PropPlacement("f", 25f, 0f, -12f, 1f, 0f, 0),       // (2, -2)
                new PropPlacement("g", 0f, 0f, 10f, 1f, 0f, 0),         // (0, 1)
                new PropPlacement("h", -0.5f, 0f, -0.5f, 1f, 0f, 0),    // (-1, -1)
            };
            var layers = new[] { PropLayer.PlacementLayer(placements, NoMeshes(), 90f) };

            Dictionary<ChunkCoord, PropPlacement[]>[]? built = PlacementBuckets.Build(layers, size);
            Assert.NotNull(built);
            Dictionary<ChunkCoord, PropPlacement[]> buckets = built![0]!;

            // Every placement sits in the bucket its own ChunkGrid.CoordOf names.
            foreach (KeyValuePair<ChunkCoord, PropPlacement[]> kv in buckets)
                foreach (PropPlacement p in kv.Value)
                    Assert.Equal(kv.Key, ChunkGrid.CoordOf(p.X, p.Z, size));

            // Exactly one chunk each: the union of the buckets is the input list as a multiset, nothing dropped or
            // duplicated at an edge.
            List<string> union = buckets.Values.SelectMany(v => v).Select(p => p.Id).OrderBy(s => s, StringComparer.Ordinal).ToList();
            Assert.Equal(placements.Select(p => p.Id).OrderBy(s => s, StringComparer.Ordinal).ToList(), union);

            // Input order survives inside a bucket.
            Assert.Equal(new[] { "c", "d", "h" }, buckets[new ChunkCoord(-1, -1)].Select(p => p.Id).ToArray());
            Assert.Equal(new[] { "a", "e" }, buckets[new ChunkCoord(0, 0)].Select(p => p.Id).ToArray());

            // Deterministic: a second Build over the same input gives the same buckets, key for key.
            Dictionary<ChunkCoord, PropPlacement[]> again = PlacementBuckets.Build(layers, size)![0]!;
            Assert.Equal(buckets.Count, again.Count);
            foreach (KeyValuePair<ChunkCoord, PropPlacement[]> kv in buckets)
                AssertSamePlacements(kv.Value, again[kv.Key]);
        }

        [Fact]
        public void Buckets_NullWhenNoPlacementLayers()
        {
            var comp = new CompanionConfig { HostKinds = new[] { "pine_a" }, Kinds = new[] { new PropKind("bush", 1f) } };
            var layers = new[]
            {
                PropLayer.ScatterLayer(new ScatterConfig(), NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
            };

            Assert.Null(PlacementBuckets.Build(layers, 60f));
        }

        [Fact]
        public void Sink_PlacementChunkLookup_TilesWholeZone()
        {
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            IReadOnlyList<PropPlacement> whole = WholeZone(field, cfg);
            Assert.NotEmpty(whole);

            var sink = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.PlacementLayer(whole, NoMeshes(), 90f) }, chunkSize: ZoneChunk);

            foreach (ChunkCoord coord in ZoneCoords())
            {
                List<PropPlacement> expected = whole.Where(p => ChunkGrid.CoordOf(p.X, p.Z, ZoneChunk) == coord).ToList();
                Assert.NotEmpty(expected);   // not vacuous: every chunk of the zone carries placements
                AssertSamePlacements(expected, sink.ScatterLayersFor(coord)[0]);
            }

            Assert.Empty(sink.ScatterLayersFor(new ChunkCoord(9, 9))[0]);   // outside the zone: nothing to serve
        }

        [Fact]
        public void Sink_PlacementLayer_MatchesScatterEquivalentPerChunk()
        {
            // The acceptance parity: a placement layer fed a config's whole-zone output streams the same placements
            // per chunk as the scatter layer that produced them, so everything downstream draws identically.
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            IReadOnlyList<PropPlacement> whole = WholeZone(field, cfg);

            var scatterSink = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.ScatterLayer(cfg, NoMeshes(), 90f) }, chunkSize: ZoneChunk);
            var placementSink = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.PlacementLayer(whole, NoMeshes(), 90f) }, chunkSize: ZoneChunk);

            foreach (ChunkCoord coord in ZoneCoords())
            {
                IReadOnlyList<PropPlacement> fromScatter = scatterSink.ScatterLayersFor(coord)[0];
                Assert.NotEmpty(fromScatter);
                AssertSamePlacements(fromScatter, placementSink.ScatterLayersFor(coord)[0]);
            }
        }

        [Fact]
        public void Sink_ScatterOnly_PathUnchanged()
        {
            // Config-off equivalence: with no placement layer in the list the sink still generates per chunk exactly
            // as before, so the bucket seam is inert.
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            var sink = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.ScatterLayer(cfg, NoMeshes(), 90f) }, chunkSize: ZoneChunk);
            var coord = new ChunkCoord(-2, 1);

            IReadOnlyList<PropPlacement> got = sink.ScatterLayersFor(coord)[0];

            Assert.NotEmpty(got);
            AssertSamePlacements(PropScatter.Generate(field, cfg, ChunkGrid.AreaOf(coord, ZoneChunk)), got);
        }

        [Fact]
        public void Sink_CompanionOffPlacementHost_DeterministicAndScatterEquivalent()
        {
            // A companion layer hosted by a PLACEMENT layer works through the same per-chunk derivation: the host
            // list for the chunk is the bucket instead of a generate, and the companions come out identical.
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            IReadOnlyList<PropPlacement> whole = WholeZone(field, cfg);
            var comp = new CompanionConfig
            {
                Seed = 7,
                HostKinds = new[] { "pine_a" },
                Kinds = new[] { new PropKind("bush", 1f) },
                CountMin = 2,
                CountMax = 3,
                RadiusMin = 0.6f,
                RadiusMax = 1.6f,
            };

            var scatterHost = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.ScatterLayer(cfg, NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
            }, chunkSize: ZoneChunk);
            var placementHost = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.PlacementLayer(whole, NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
            }, chunkSize: ZoneChunk);

            foreach (ChunkCoord coord in ZoneCoords())
            {
                IReadOnlyList<PropPlacement> companions = placementHost.ScatterLayersFor(coord)[1];
                Assert.NotEmpty(companions);
                AssertSamePlacements(scatterHost.ScatterLayersFor(coord)[1], companions);
                AssertSamePlacements(companions, placementHost.ScatterLayersFor(coord)[1]);   // determinism
            }
        }

        [Fact]
        public void LayerRegistersColliders_Rules()
        {
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            var comp = new CompanionConfig { HostKinds = new[] { "pine_a" }, Kinds = new[] { new PropKind("bush", 1f) } };

            Scene3DChunkSink Sink(params PropLayer[] layers) =>
                new Scene3DChunkSink(scene: null!, field, layers, chunkSize: ZoneChunk);

            // Scatter layers keep the long-standing rule: only layer 0's props register.
            var twoScatter = Sink(PropLayer.ScatterLayer(cfg, NoMeshes(), 90f), PropLayer.ScatterLayer(cfg, NoMeshes(), 40f));
            Assert.True(twoScatter.LayerRegistersColliders(0));
            Assert.False(twoScatter.LayerRegistersColliders(1));

            // A placement layer follows its own flag, wherever it sits.
            var placementOn = Sink(PropLayer.PlacementLayer(placements, NoMeshes(), 90f));
            Assert.True(placementOn.LayerRegistersColliders(0));

            var placementOff = Sink(PropLayer.PlacementLayer(placements, NoMeshes(), 90f, colliders: false));
            Assert.False(placementOff.LayerRegistersColliders(0));

            var scatterThenPlacement = Sink(
                PropLayer.ScatterLayer(cfg, NoMeshes(), 90f),
                PropLayer.PlacementLayer(placements, NoMeshes(), 40f));
            Assert.True(scatterThenPlacement.LayerRegistersColliders(0));
            Assert.True(scatterThenPlacement.LayerRegistersColliders(1));

            var scatterThenCompanion = Sink(
                PropLayer.ScatterLayer(cfg, NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f));
            Assert.True(scatterThenCompanion.LayerRegistersColliders(0));
            Assert.False(scatterThenCompanion.LayerRegistersColliders(1));
        }

        [Fact]
        public void Sink_PlacementValidation()
        {
            TerrainField field = Flat(5f);
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            var comp = new CompanionConfig { HostKinds = new[] { "tree" }, Kinds = new[] { new PropKind("bush", 1f) } };

            // A placement layer alone is a valid layer list (it has no Scatter and no Companions).
            var sink = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.PlacementLayer(placements, NoMeshes(), 90f) }, chunkSize: ZoneChunk);
            Assert.Single(sink.ScatterLayersFor(new ChunkCoord(0, 0)));

            // A layer carrying none of the three still fails.
            Assert.Throws<ArgumentException>(() =>
                new Scene3DChunkSink(scene: null!, field, new[] { default(PropLayer) }, chunkSize: ZoneChunk));

            // A companion may host off a placement layer.
            var hosted = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.PlacementLayer(placements, NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
            }, chunkSize: ZoneChunk);
            Assert.Equal(2, hosted.ScatterLayersFor(new ChunkCoord(0, 0)).Length);
        }

        // --- HLOD / decor / fade-LOD downstream parity (issue #276 L3 + #286) ---------------------------------------
        // The bucket seam above makes BuildCpu, Apply, and Draw shared code between a scatter layer and a placement
        // layer fed its output. These lock that the sharing actually holds where it matters most: the HLOD merge
        // bake, the decor-ring behaviour, the off path when no layer opts into HLOD, and the real fade/LOD selection
        // PropRenderer.Queue performs when drawing.

        static void AssertMeshByteIdentical(GltfMesh a, GltfMesh b)
        {
            Assert.Equal(a.Vertices.Length, b.Vertices.Length);
            Assert.Equal(a.Indices32.Length, b.Indices32.Length);
            for (int i = 0; i < a.Vertices.Length; i++)
            {
                Assert.Equal(a.Vertices[i].Position, b.Vertices[i].Position);
                Assert.Equal(a.Vertices[i].Normal, b.Vertices[i].Normal);
                Assert.Equal(a.Vertices[i].Color, b.Vertices[i].Color);
            }
            for (int i = 0; i < a.Indices32.Length; i++) Assert.Equal(a.Indices32[i], b.Indices32[i]);
        }

        [Fact]
        public void BuildCpu_HlodParity_PlacementVsScatter()
        {
            // The HLOD merge reads the same per-chunk scatter the render props do, so a placement layer fed a
            // scattered zone's whole output must bake a byte-identical HLOD mesh to the scatter layer that produced
            // it, at every chunk of the zone.
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            IReadOnlyList<PropPlacement> whole = WholeZone(field, cfg);
            var source = new Dictionary<string, GltfMesh> { ["pine_a"] = MeshPrimitives.Box(1.5f) };

            var scatterSink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.ScatterLayer(cfg, NoMeshes(), 90f).WithHlod(source, hlodDistance: 50f, weldCell: 2f),
            }, chunkSize: ZoneChunk);
            var placementSink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.PlacementLayer(whole, NoMeshes(), 90f).WithHlod(source, hlodDistance: 50f, weldCell: 2f),
            }, chunkSize: ZoneChunk);

            foreach (ChunkCoord coord in ZoneCoords())
            {
                var scatterCpu = (Scene3DChunkSink.CpuBuild)scatterSink.BuildCpu(coord, lod: 0, ChunkRing.Gameplay);
                var placementCpu = (Scene3DChunkSink.CpuBuild)placementSink.BuildCpu(coord, lod: 0, ChunkRing.Gameplay);

                Assert.NotNull(scatterCpu.HlodMeshes![0]);
                Assert.NotNull(placementCpu.HlodMeshes![0]);
                AssertMeshByteIdentical(scatterCpu.HlodMeshes[0]!, placementCpu.HlodMeshes[0]!);
            }
        }

        [Fact]
        public void BuildCpu_DecorRing_PlacementLayer_BakesHlodWithoutProps()
        {
            // A placement layer's decor ring behaves exactly like a scatter layer's (Scene3DChunkSinkTests'
            // BuildCpu_WithHlodLayer_BakesMergedMeshForGameplayAndDecor): render-only, so LayerProps carries no
            // individual props, but the merged mesh still stands in for them - and it is the SAME mesh the gameplay
            // ring bakes for that chunk, since the merge is a pure function of the chunk's placements, not the ring.
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            IReadOnlyList<PropPlacement> whole = WholeZone(field, cfg);
            var source = new Dictionary<string, GltfMesh> { ["pine_a"] = MeshPrimitives.Box(1.5f) };
            PropLayer layer = PropLayer.PlacementLayer(whole, NoMeshes(), 90f).WithHlod(source, hlodDistance: 50f, weldCell: 2f);
            var sink = new Scene3DChunkSink(scene: null!, field, new[] { layer }, chunkSize: ZoneChunk);
            var coord = new ChunkCoord(0, 0);

            var gameplay = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 0, ChunkRing.Gameplay);
            var decor = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 2, ChunkRing.Decor);

            Assert.Single(decor.LayerProps);
            Assert.Empty(decor.LayerProps[0]);
            Assert.NotNull(decor.HlodMeshes![0]);
            AssertMeshByteIdentical(gameplay.HlodMeshes![0]!, decor.HlodMeshes[0]!);
        }

        [Fact]
        public void BuildCpu_NoHlod_PlacementLayer_NoHlodMeshes()
        {
            // Config-off equivalence for a placement-only layer list: with no layer opted into WithHlod, BuildCpu
            // produces no HLOD meshes at all (locks the sink's _anyHlod off path), mirroring the scatter-layer
            // equivalent Scene3DChunkSinkTests.BuildCpu_WithNoHlodLayer_ProducesNoHlodMeshes.
            TerrainField field = Flat(5f);
            IReadOnlyList<PropPlacement> placements = OnePlacement();
            var sink = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.PlacementLayer(placements, NoMeshes(), 90f) }, chunkSize: ZoneChunk);

            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(new ChunkCoord(0, 0), lod: 0);

            Assert.Null(cpu.HlodMeshes);
        }

        [Fact]
        public void Queue_FadeLodSelectionParity_PlacementVsScatter()
        {
            // The fade/LOD selection code itself (PropRenderer.Queue) is exercised headlessly in PropRendererTests;
            // this proves it selects and dissolves IDENTICALLY when fed a placement layer's chunk bucket vs the
            // scatter layer that produced it (issue #286) - the real selection code, not just a comparison of the
            // raw PropPlacement lists (already locked by Sink_PlacementLayer_MatchesScatterEquivalentPerChunk).
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            IReadOnlyList<PropPlacement> whole = WholeZone(field, cfg);
            var meshes = new Dictionary<string, MeshHandle> { ["pine_a"] = new MeshHandle(3) };
            var lodMeshes = new Dictionary<string, MeshHandle> { ["pine_a"] = new MeshHandle(9) };
            const float drawRadius = 60f;
            const float fadeBandWidth = 40f;   // fade starts at 20: spans solid, fading, and out-of-range placements
            const float lodDistance = 25f;     // spans full-mesh-near and lod-mesh-far placements too

            var scatterSink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.ScatterLayer(cfg, meshes, drawRadius, fadeBandWidth, lodMeshes, lodDistance),
            }, chunkSize: ZoneChunk);
            var placementSink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.PlacementLayer(whole, meshes, drawRadius, fadeBandWidth, lodMeshes, lodDistance),
            }, chunkSize: ZoneChunk);

            var coord = new ChunkCoord(0, 0);
            IReadOnlyList<PropPlacement> fromScatter = scatterSink.ScatterLayersFor(coord)[0];
            IReadOnlyList<PropPlacement> fromPlacement = placementSink.ScatterLayersFor(coord)[0];
            Assert.NotEmpty(fromScatter);

            var focus = new Vector3(30f, 0f, 30f);   // chunk centre: distances span both the fade band and the LOD swap

            var scatterQueue = new SceneInstances();
            int scatterCount = PropRenderer.Queue(scatterQueue, fromScatter, meshes, focus, drawRadius,
                fadeBandWidth: fadeBandWidth, lodMeshes: lodMeshes, lodDistance: lodDistance);
            var placementQueue = new SceneInstances();
            int placementCount = PropRenderer.Queue(placementQueue, fromPlacement, meshes, focus, drawRadius,
                fadeBandWidth: fadeBandWidth, lodMeshes: lodMeshes, lodDistance: lodDistance);

            Assert.Equal(scatterCount, placementCount);
            Assert.Equal(scatterQueue.Items.Count, placementQueue.Items.Count);
            for (int i = 0; i < scatterQueue.Items.Count; i++)
            {
                Assert.Equal(scatterQueue.Items[i].Mesh.Index, placementQueue.Items[i].Mesh.Index);
                Assert.Equal(scatterQueue.Items[i].World, placementQueue.Items[i].World);
                Assert.Equal(scatterQueue.Items[i].DissolveThreshold, placementQueue.Items[i].DissolveThreshold, 5);
                Assert.Equal(scatterQueue.Items[i].Tint, placementQueue.Items[i].Tint);
            }
        }

        [GpuFact]
        public void Placement_layer_statics_follow_its_collider_flag() => WithScene(scene =>
        {
            // End-to-end through Apply: a placement layer above index 0 registers its props as static bodies when it
            // keeps colliders on, and registers none when it opts out - while layer 0's scatter statics are the same
            // either way. GPU-gated because Apply always uploads through a real Scene3D.
            TerrainField field = Flat(5f);
            ScatterConfig cfg = NoJitterKind("pine_a", seed: 3, cell: ZoneCell);
            var placements = new[]
            {
                new PropPlacement("rock", 5f, 5f, 5f, 1f, 0f, 0),
                new PropPlacement("rock", 20f, 5f, 30f, 1f, 0f, 0),
            };
            var shapes = new Dictionary<string, PhysicsShape>
            {
                ["pine_a"] = new BoxShape(new Vector3(0.5f, 1f, 0.5f)),
                ["rock"] = new BoxShape(new Vector3(0.4f, 0.4f, 0.4f)),
            };
            var coord = new ChunkCoord(0, 0);

            var onWorld = new FakePhysicsWorld();
            var on = new Scene3DChunkSink(scene, field, new[]
            {
                PropLayer.ScatterLayer(cfg, NoMeshes(), 90f),
                PropLayer.PlacementLayer(placements, NoMeshes(), 40f),
            }, chunkSize: ZoneChunk, physics: onWorld, collisionShapes: shapes);

            var offWorld = new FakePhysicsWorld();
            var off = new Scene3DChunkSink(scene, field, new[]
            {
                PropLayer.ScatterLayer(cfg, NoMeshes(), 90f),
                PropLayer.PlacementLayer(placements, NoMeshes(), 40f, colliders: false),
            }, chunkSize: ZoneChunk, physics: offWorld, collisionShapes: shapes);

            var loadOn = (Scene3DChunkSink.ChunkLoad)on.Load(coord, lod: 0);
            var loadOff = (Scene3DChunkSink.ChunkLoad)off.Load(coord, lod: 0);

            Assert.Equal(2, loadOn.LayerProps[1].Count);                    // both placements land in chunk (0, 0)
            Assert.Equal(loadOff.Statics.Count + 2, loadOn.Statics.Count);  // exactly the two placement bodies extra
            Assert.NotEmpty(loadOff.Statics);                               // layer 0's scatter statics, unchanged
            Assert.Equal(loadOn.Statics.Count, onWorld.Added.Count);
            Assert.Equal(loadOff.Statics.Count, offWorld.Added.Count);
        });
    }
}
