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
            // Vertices are CHUNK-LOCAL in X/Z since the chunk-local bake, so the field is sampled at the vertex
            // plus the region origin. Y stays absolute world height.
            var field = Field();
            TerrainChunkRegion region = Region();
            var chunk = TerrainChunkBuilder.Build(field, region, lod: 1);
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                var v = chunk.Mesh.Vertices[i].Position;
                Assert.Equal(field.SampleHeight(v.X + region.OriginX, v.Z + region.OriginZ), v.Y, 3);
            }
        }

        [Fact]
        public void Vertices_are_chunk_local_however_far_out_the_chunk_sits()
        {
            // The release-2 headline, as an assertion on the buffer: a chunk 100 km out has vertices no larger than
            // its own size, so nothing is quantized to that magnitude's float32 lattice at bake time. The old bake
            // wrote 100 km into every vertex, where one ULP is 7.8 mm, and no camera-relative render or physics
            // rebase could recover what the buffer had already lost.
            var field = Field();
            const float far = 100_000f;
            var region = new TerrainChunkRegion { OriginX = far, OriginZ = far, Size = 60f };
            var chunk = TerrainChunkBuilder.Build(field, region, lod: 1);

            foreach (var v in chunk.Mesh.Vertices)
            {
                Assert.InRange(v.Position.X, 0f, region.Size);
                Assert.InRange(v.Position.Z, 0f, region.Size);
            }
            // The bounds follow the vertices, so they are chunk-local too (offset by the region origin for a world
            // box). Y is absolute on both.
            Assert.InRange(chunk.Bounds.Min.X, 0f, region.Size);
            Assert.InRange(chunk.Bounds.Max.Z, 0f, region.Size);

            // And the geometry is EXACTLY what the same chunk shape produces at the origin: same field shape (the
            // Clearing preset is not translation-invariant, so heights differ), but the planar lattice is identical
            // rather than snapped to a coarser one.
            var atOrigin = TerrainChunkBuilder.Build(field, new TerrainChunkRegion { OriginX = 0f, OriginZ = 0f, Size = 60f }, lod: 1);
            for (int i = 0; i < chunk.SurfaceVertexCount; i++)
            {
                Assert.Equal(atOrigin.Mesh.Vertices[i].Position.X, chunk.Mesh.Vertices[i].Position.X);
                Assert.Equal(atOrigin.Mesh.Vertices[i].Position.Z, chunk.Mesh.Vertices[i].Position.Z);
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
                var pa = a.Mesh.Vertices[iz * cols + res].Position;   // a, ix = res (world x = 60, local 60)
                var pb = b.Mesh.Vertices[iz * cols + 0].Position;     // b, ix = 0   (world x = 60, local 0)
                Assert.Equal(pa.Y, pb.Y, 4);
            }
        }
    }
}
