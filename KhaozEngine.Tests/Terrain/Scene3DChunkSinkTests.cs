using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Headless tests for the multi-layer chunk sink + the PropLayer tagged struct (no GPU - all assertions
    /// go through the internal ScatterLayersFor / ScatterFor accessors and the PropLayer factories).</summary>
    public class Scene3DChunkSinkTests
    {
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
    }
}
