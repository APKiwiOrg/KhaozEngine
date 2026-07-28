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

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void Buffers_are_sized_exactly_and_filled_completely(int lod)
        {
            // Issue #393: the builder writes into exactly-sized arrays instead of growing three lists and copying
            // each one out. That only holds if the counting formula matches what the loops actually write, and the
            // failure mode of getting it wrong is silent: an oversized buffer leaves a tail of default vertices and
            // a tail of (0,0,0) triangles, which renders as a degenerate sliver at the chunk origin rather than
            // throwing. So pin the sizes, and pin that nothing degenerate survives anywhere in the index buffer.
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod);
            int res = TerrainLod.ResolutionFor(lod), cols = res + 1;

            Assert.Equal(cols * cols + cols * 4, chunk.Mesh.Vertices.Length);
            Assert.Equal(cols * cols + cols * 4, chunk.Splat.Length);
            Assert.Equal(res * res * 6 + res * 4 * 6, chunk.Mesh.Indices32.Length);

            uint[] idx = chunk.Mesh.Indices32;
            for (int t = 0; t < idx.Length; t += 3)
            {
                uint a = idx[t], b = idx[t + 1], c = idx[t + 2];
                Assert.True(a != b && b != c && a != c, $"degenerate triangle at index {t}: an unfilled buffer tail");
                Assert.True(a < chunk.Mesh.Vertices.Length && b < chunk.Mesh.Vertices.Length && c < chunk.Mesh.Vertices.Length);
            }
        }

        [Fact]
        public void Skirt_vertices_are_the_edge_vertices_dropped_by_the_skirt_depth()
        {
            // The four skirts are written through the same cursor as the surface, so a cursor that lost its place
            // would put an edge copy somewhere other than where its quads expect it. Walk the -Z edge and check the
            // dropped copy sits directly under its own top vertex.
            var chunk = TerrainChunkBuilder.Build(Field(), Region(), lod: 1);
            int res = TerrainLod.ResolutionFor(1), cols = res + 1;
            int firstSkirt = cols * cols;   // the -Z edge, written first

            for (int ix = 0; ix <= res; ix++)
            {
                var top = chunk.Mesh.Vertices[ix].Position;              // iz = 0 row
                var low = chunk.Mesh.Vertices[firstSkirt + ix].Position;
                Assert.Equal(top.X, low.X, 5);
                Assert.Equal(top.Z, low.Z, 5);
                Assert.Equal(top.Y - 0.3f, low.Y, 5);                    // the default skirtDepth
                Assert.Equal(chunk.Splat[ix].Grass, chunk.Splat[firstSkirt + ix].Grass, 5);
            }
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
