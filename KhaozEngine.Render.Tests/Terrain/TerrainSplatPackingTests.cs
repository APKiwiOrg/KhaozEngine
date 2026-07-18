using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainSplatPackingTests
    {
        [Fact]
        public void PackThenUnpackRoundTripsTheFiveWeights()
        {
            var w = TerrainSplatWeights.From(height: 30f, slope01: 0.3f, biome: default, waterLevel: 0f, snowLine: 60f);
            Vector4 packed = TerrainSplatPacking.Pack(w);
            var (g, d, r, s, snow) = SplatMath.UnpackWeights(packed);
            Assert.Equal(w.Grass, g, 4);
            Assert.Equal(w.Dirt, d, 4);
            Assert.Equal(w.Rock, r, 4);
            Assert.Equal(w.Sand, s, 4);
            Assert.Equal(w.Snow, snow, 4);
        }

        [Fact]
        public void PackedMeshCarriesWeightsInColorForEveryVertex()
        {
            var field = new TerrainField(TerrainPresets.Clearing());
            var region = new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 32f };
            var chunk = TerrainChunkBuilder.Build(field, region, lod: 0);
            var packed = TerrainSplatPacking.PackedMesh(chunk);

            Assert.Equal(chunk.Mesh.Vertices.Length, packed.Vertices.Length);
            for (int i = 0; i < packed.Vertices.Length; i++)
            {
                Assert.Equal(TerrainSplatPacking.Pack(chunk.Splat[i]), packed.Vertices[i].Color);
                Assert.Equal(chunk.Mesh.Vertices[i].Position, packed.Vertices[i].Position);
                Assert.Equal(chunk.Mesh.Vertices[i].Normal, packed.Vertices[i].Normal);
                Assert.Equal(chunk.Mesh.Vertices[i].Uv, packed.Vertices[i].Uv);
                Assert.Equal(chunk.Mesh.Vertices[i].Tangent, packed.Vertices[i].Tangent);
            }
        }

        [Fact]
        public void ProceduralMaterialValidates()
        {
            var mat = TerrainMaterialPresets.Procedural(size: 16);
            mat.Validate();   // throws on a malformed material; reaching here is the assertion
            Assert.Equal(5, mat.Layers.Count);
            Assert.Equal(16 * 16 * 4, mat.Grass.AlbedoRgba.Length);
        }
    }
}
