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
            PropLayer layer = PropLayer.ScatterLayer(cfg, NoMeshes(), 90f);

            Assert.False(layer.IsCompanion);
            Assert.Same(cfg, layer.Scatter);
            Assert.Null(layer.Companions);
            Assert.Equal(90f, layer.DrawRadius);
        }

        [Fact]
        public void CompanionLayer_factory_sets_a_companion_layer()
        {
            var comp = new CompanionConfig { HostKinds = new[] { "pine_a" }, Kinds = new[] { new PropKind("bush", 1f) } };
            PropLayer layer = PropLayer.CompanionLayer(0, comp, NoMeshes(), 40f);

            Assert.True(layer.IsCompanion);
            Assert.Same(comp, layer.Companions);
            Assert.Null(layer.Scatter);
            Assert.Equal(0, layer.HostLayerIndex);
            Assert.Equal(40f, layer.DrawRadius);
        }
    }
}
