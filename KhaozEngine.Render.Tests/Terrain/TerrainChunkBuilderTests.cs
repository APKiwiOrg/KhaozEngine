using System;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    public class TerrainChunkBuilderTests
    {
        static TerrainField Field() => new TerrainField(TerrainPresets.Clearing());
        static TerrainChunkRegion Region() => new TerrainChunkRegion { OriginX = -30f, OriginZ = -30f, Size = 60f };

        [Fact]
        public void Surface_vertex_count_matches_the_lod_grid()
        {
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1);
            int res = TerrainLod.ResolutionFor(1);
            Assert.Equal((res + 1) * (res + 1), chunk.SurfaceVertexCount);
        }

        [Fact]
        public void Denser_lod_has_more_surface_vertices()
        {
            var near = TerrainChunkBuilder.Build(Field(), Region(), lod: 0);
            var far = TerrainChunkBuilder.Build(Field(), Region(), lod: 2);
            Assert.True(near.SurfaceVertexCount > far.SurfaceVertexCount);
        }

        [Fact]
        public void Mesh_vertex_heights_equal_the_field()
        {
            var field = Field();
            var chunk = TerrainChunkBuilder.Build(field, Region(), lod: 1);
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                var v = chunk.Mesh.Vertices[i].Position;
                Assert.Equal(field.SampleHeight(v.X, v.Z), v.Y, 3);
            }
        }

        [Fact]
        public void Skirt_adds_vertices_below_the_surface()
        {
            const float skirt = 0.3f;
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1, skirtDepth: skirt);
            Assert.True(chunk.Mesh.Vertices.Length > chunk.SurfaceVertexCount);   // skirt vertices present

            // The skirt hangs skirtDepth below the chunk EDGE ring (interior vertices like the lake basin keep no
            // skirt), so compare against the edge minimum, not the global surface minimum.
            int res = TerrainLod.ResolutionFor(1), cols = res + 1;
            float edgeMinY = float.MaxValue;
            for (int iz = 0; iz <= res; iz++)
            for (int ix = 0; ix <= res; ix++)
                if (ix == 0 || ix == res || iz == 0 || iz == res)
                    edgeMinY = MathF.Min(edgeMinY, chunk.Mesh.Vertices[iz * cols + ix].Position.Y);

            float skirtMinY = float.MaxValue;
            for (int i = chunk.SurfaceVertexCount; i < chunk.Mesh.Vertices.Length; i++)
                skirtMinY = MathF.Min(skirtMinY, chunk.Mesh.Vertices[i].Position.Y);

            Assert.Equal(edgeMinY - skirt, skirtMinY, 3);        // skirt is exactly skirtDepth below the edge
            Assert.True(chunk.Bounds.Min.Y <= skirtMinY);        // bounds reach the dropped skirt
        }

        [Fact]
        public void Bounds_enclose_every_vertex()
        {
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1);
            foreach (var v in chunk.Mesh.Vertices)
                Assert.True(chunk.Bounds.Contains(v.Position));
        }

        [Fact]
        public void Splat_array_parallels_the_vertices()
        {
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 2);
            Assert.Equal(chunk.Mesh.Vertices.Length, chunk.Splat.Length);
        }

        [Fact]
        public void Adjacent_chunks_share_identical_edge_heights()
        {
            // statelessness/locality at the chunk seam: the +X edge of one chunk equals the -X edge of its neighbour.
            var field = Field();
            var a = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 60f }, lod: 1);
            var b = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 60f, OriginZ = 0f, Size = 60f }, lod: 1);
            int res = TerrainLod.ResolutionFor(1), cols = res + 1;
            for (int iz = 0; iz <= res; iz++)
            {
                var pa = a.Mesh.Vertices[iz * cols + res].Position;   // a, ix = res (x=60)
                var pb = b.Mesh.Vertices[iz * cols + 0].Position;     // b, ix = 0   (x=60)
                Assert.Equal(pa.Y, pb.Y, 4);
            }
        }
    }
}
