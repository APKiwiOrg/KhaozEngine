using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Covers the live <see cref="IPlacementSource"/> seam: a source-backed
    /// <see cref="PropLayer.PlacementLayer(IPlacementSource, IReadOnlyDictionary{string, MeshHandle}, float, float, IReadOnlyDictionary{string, MeshHandle}, float, bool)"/>
    /// is queried at EVERY chunk build, so content that arrives after the sink was constructed reaches the render
    /// path, which a frozen bucketed list cannot do. The frozen path stays byte-identical.</summary>
    public class PlacementSourceLayerTests
    {
        static IReadOnlyDictionary<string, MeshHandle> NoMeshes() => new Dictionary<string, MeshHandle>();

        static IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>> NoPartMeshes() =>
            new Dictionary<string, IReadOnlyList<MeshHandle>>();

        /// <summary>A source whose contents change between builds, exactly as a residency layer's do. Publishes a
        /// whole immutable array on every mutation, which is the discipline the interface asks of an
        /// implementation.</summary>
        sealed class MutableSource : IPlacementSource
        {
            volatile PropPlacement[] _published = Array.Empty<PropPlacement>();
            public int Queries;

            public void Publish(params PropPlacement[] placements) => _published = placements;

            public void PlacementsIn(RectArea area, List<PropPlacement> into)
            {
                Queries++;
                PropPlacement[] snapshot = _published;
                for (int i = 0; i < snapshot.Length; i++)
                {
                    PropPlacement p = snapshot[i];
                    if (p.X >= area.MinX && p.X < area.MaxX && p.Z >= area.MinZ && p.Z < area.MaxZ) into.Add(p);
                }
            }
        }

        static TerrainField Flat(float height) => new(new TerrainConfig
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

        const float Chunk = 60f;

        [Fact]
        public void PlacementLayer_Source_NullArgsThrow()
        {
            var source = new MutableSource();
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer((IPlacementSource)null!, NoMeshes(), 90f));
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer((IPlacementSource)null!, NoPartMeshes(), 90f));
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(source, (IReadOnlyDictionary<string, MeshHandle>)null!, 90f));
            Assert.Throws<ArgumentNullException>(() =>
                PropLayer.PlacementLayer(source, (IReadOnlyDictionary<string, IReadOnlyList<MeshHandle>>)null!, 90f));
        }

        [Fact]
        public void PlacementLayer_Source_StoresKnobsAndIsAPlacementLayer()
        {
            var source = new MutableSource();
            PropLayer layer = PropLayer.PlacementLayer(source, NoMeshes(), 120f, 15f, NoMeshes(), 60f);

            Assert.Same(source, layer.PlacementSource);
            Assert.Null(layer.Placements);            // exactly one of the two is set
            Assert.True(layer.IsPlacement);
            Assert.False(layer.IsCompanion);
            Assert.True(layer.RegisterColliders);
            Assert.Equal(120f, layer.DrawRadius);
            Assert.Equal(15f, layer.FadeBandWidth);
            Assert.Equal(60f, layer.LodDistance);

            Assert.False(PropLayer.PlacementLayer(source, NoMeshes(), 90f, colliders: false).RegisterColliders);

            PropLayer multi = PropLayer.PlacementLayer(source, NoPartMeshes(), 90f);
            Assert.Same(source, multi.PlacementSource);
            Assert.Empty(multi.Meshes);
            Assert.NotNull(multi.PartMeshes);
        }

        [Fact]
        public void WithHlod_CarriesThePlacementSource()
        {
            var source = new MutableSource();
            PropLayer layer = PropLayer.PlacementLayer(source, NoMeshes(), 90f, colliders: false)
                                       .WithHlod(new Dictionary<string, GltfMesh>(), hlodDistance: 120f, weldCell: 2f);

            Assert.Same(source, layer.PlacementSource);
            Assert.True(layer.IsPlacement);
            Assert.False(layer.RegisterColliders);
            Assert.True(layer.HasHlod);
        }

        [Fact]
        public void Buckets_SkipSourceBackedLayers()
        {
            // Bucketing a source-backed layer once at construction is precisely the staleness the seam exists to
            // avoid, so Build must not produce a bucket map for it at all.
            var layers = new[] { PropLayer.PlacementLayer(new MutableSource(), NoMeshes(), 90f) };
            Assert.Null(PlacementBuckets.Build(layers, Chunk));
        }

        [Fact]
        public void StreamedPlacements_ReachTheSink()
        {
            // The point of the whole seam. A placement that did not exist when the sink was built is served by the
            // very next chunk build, which is what TerrainStreamer.Invalidate re-runs on a tile arrival. With a
            // frozen list (bucketed once at construction) this is impossible, which is the regression being pinned.
            TerrainField field = Flat(5f);
            var source = new MutableSource();
            var sink = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.PlacementLayer(source, NoMeshes(), 90f) }, chunkSize: Chunk);
            var coord = new ChunkCoord(0, 0);

            Assert.Empty(sink.ScatterLayersFor(coord)[0]);

            var arrived = new PropPlacement("hut", 10f, 5f, 20f, 1f, 0.5f, 0);
            source.Publish(arrived);

            IReadOnlyList<PropPlacement> served = sink.ScatterLayersFor(coord)[0];
            PropPlacement got = Assert.Single(served);
            Assert.Equal("hut", got.Id);
            Assert.Equal(10f, got.X, 4);
            Assert.Equal(20f, got.Z, 4);

            // And the same list reaches the CPU build, which is what a re-LOD / invalidate rebuild consumes.
            var cpu = (Scene3DChunkSink.CpuBuild)sink.BuildCpu(coord, lod: 0, ChunkRing.Gameplay);
            Assert.Single(cpu.LayerProps[0]);

            // A departure is served just as promptly.
            source.Publish();
            Assert.Empty(sink.ScatterLayersFor(coord)[0]);
            Assert.True(source.Queries >= 4, "the sink must query the source per build, not cache it.");
        }

        [Fact]
        public void SourceLayer_MatchesTheFrozenListPerChunk()
        {
            // Parity: over static content the two placement-layer kinds are indistinguishable per chunk, so the
            // frozen path keeps every downstream behaviour (HLOD bake, fade/LOD selection) it already had.
            TerrainField field = Flat(5f);
            var placements = new[]
            {
                new PropPlacement("a", 0f, 5f, 0f, 1f, 0f, 0),          // exact origin edge -> (0, 0)
                new PropPlacement("b", 60f, 5f, 0f, 1f, 0f, 0),         // exact edge x = chunkSize -> (1, 0)
                new PropPlacement("c", -1f, 5f, -1f, 1f, 0f, 0),        // negative -> (-1, -1)
                new PropPlacement("d", 59.9f, 5f, 30f, 1f, 0f, 0),      // just inside (0, 0)
            };
            var source = new MutableSource();
            source.Publish(placements);

            var frozen = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.PlacementLayer(placements, NoMeshes(), 90f) }, chunkSize: Chunk);
            var live = new Scene3DChunkSink(scene: null!, field,
                new[] { PropLayer.PlacementLayer(source, NoMeshes(), 90f) }, chunkSize: Chunk);

            var seen = new List<string>();
            for (int cx = -1; cx <= 1; cx++)
            for (int cz = -1; cz <= 1; cz++)
            {
                var coord = new ChunkCoord(cx, cz);
                IReadOnlyList<PropPlacement> a = frozen.ScatterLayersFor(coord)[0];
                IReadOnlyList<PropPlacement> b = live.ScatterLayersFor(coord)[0];
                Assert.Equal(a.Select(p => p.Id).ToArray(), b.Select(p => p.Id).ToArray());
                seen.AddRange(b.Select(p => p.Id));
            }

            // The half-open seam holds both ways: every placement lands in exactly one chunk, none twice.
            Assert.Equal(new[] { "a", "b", "c", "d" }, seen.OrderBy(s => s, StringComparer.Ordinal).ToArray());
        }

        [Fact]
        public void SourceLayer_HostsACompanionLayer()
        {
            // A companion layer derives from its host's per-chunk placements, so it works off a live source with
            // no extra plumbing: the seam is shared.
            TerrainField field = Flat(5f);
            var source = new MutableSource();
            source.Publish(new PropPlacement("pine_a", 10f, 5f, 10f, 1f, 0f, 0));
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

            var sink = new Scene3DChunkSink(scene: null!, field, new[]
            {
                PropLayer.PlacementLayer(source, NoMeshes(), 90f),
                PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f),
            }, chunkSize: Chunk);

            Assert.NotEmpty(sink.ScatterLayersFor(new ChunkCoord(0, 0))[1]);
            Assert.Empty(sink.ScatterLayersFor(new ChunkCoord(5, 5))[1]);
        }
    }
}
