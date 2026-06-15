using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshPrimitivesTests
    {
        [Fact]
        public void Box_Has_24_Vertices_And_36_Indices()
        {
            var box = MeshPrimitives.Box();
            Assert.Equal(24, box.Vertices.Length);
            Assert.Equal(36, box.Indices.Length);
        }

        [Fact]
        public void Box_Indices_Are_All_In_Range()
        {
            var box = MeshPrimitives.Box();
            foreach (var idx in box.Indices)
                Assert.InRange(idx, 0, box.Vertices.Length - 1);
        }

        [Fact]
        public void Box_Has_8_Distinct_Corners_At_HalfSize()
        {
            const float size = 2f;
            float h = size * 0.5f;
            var box = MeshPrimitives.Box(size);

            var corners = box.Vertices.Select(v => v.Position).Distinct().ToList();
            Assert.Equal(8, corners.Count);
            foreach (var c in corners)
            {
                Assert.Equal(h, System.MathF.Abs(c.X), 4);
                Assert.Equal(h, System.MathF.Abs(c.Y), 4);
                Assert.Equal(h, System.MathF.Abs(c.Z), 4);
            }
        }

        [Fact]
        public void Tile_Base_Sits_At_Y0_And_Top_At_Thickness()
        {
            const float thickness = 0.25f;
            var tile = MeshPrimitives.Tile(1f, thickness);

            float minY = tile.Vertices.Min(v => v.Position.Y);
            float maxY = tile.Vertices.Max(v => v.Position.Y);
            Assert.Equal(0f, minY, 4);
            Assert.Equal(thickness, maxY, 4);
        }

        [Fact]
        public void Primitive_Vertex_Colors_Are_White()
        {
            foreach (var mesh in new[]
            {
                MeshPrimitives.Box(), MeshPrimitives.Tile(),
                MeshPrimitives.Cylinder(), MeshPrimitives.Cone(),
                MeshPrimitives.Pyramid(), MeshPrimitives.Wedge(), MeshPrimitives.Sphere(),
            })
                foreach (var v in mesh.Vertices)
                    Assert.Equal(Vector4.One, v.Color);
        }

        // --- shared invariants for every new primitive ---

        public static System.Collections.Generic.IEnumerable<object[]> AllNewPrimitives()
        {
            yield return new object[] { MeshPrimitives.Cylinder() };
            yield return new object[] { MeshPrimitives.Cylinder(capped: false) };
            yield return new object[] { MeshPrimitives.Cone() };
            yield return new object[] { MeshPrimitives.Cone(capped: false) };
            yield return new object[] { MeshPrimitives.Pyramid() };
            yield return new object[] { MeshPrimitives.Wedge() };
            yield return new object[] { MeshPrimitives.Sphere() };
        }

        [Theory]
        [MemberData(nameof(AllNewPrimitives))]
        public void Primitive_IndexCount_Divisible_By_3(GltfMesh mesh)
        {
            Assert.Equal(0, mesh.Indices.Length % 3);
        }

        [Theory]
        [MemberData(nameof(AllNewPrimitives))]
        public void Primitive_Indices_Are_All_In_Range(GltfMesh mesh)
        {
            foreach (var idx in mesh.Indices)
                Assert.InRange(idx, 0, mesh.Vertices.Length - 1);
        }

        [Theory]
        [MemberData(nameof(AllNewPrimitives))]
        public void Primitive_Normals_Are_Unit_Length(GltfMesh mesh)
        {
            foreach (var v in mesh.Vertices)
                Assert.Equal(1f, v.Normal.Length(), 3);
        }

        [Fact]
        public void Cylinder_Spans_Y_0_To_Height()
        {
            const float height = 2f;
            const float radius = 0.75f;
            var c = MeshPrimitives.Cylinder(radius, height);
            Assert.Equal(0f, c.Vertices.Min(v => v.Position.Y), 4);
            Assert.Equal(height, c.Vertices.Max(v => v.Position.Y), 4);
            foreach (var v in c.Vertices)
            {
                float r = System.MathF.Sqrt(v.Position.X * v.Position.X + v.Position.Z * v.Position.Z);
                Assert.InRange(r, 0f, radius + 1e-3f);
            }
        }

        [Fact]
        public void Cylinder_Uncapped_Has_Fewer_Verts_And_Indices()
        {
            var capped = MeshPrimitives.Cylinder(capped: true);
            var open = MeshPrimitives.Cylinder(capped: false);
            Assert.True(open.Vertices.Length < capped.Vertices.Length);
            Assert.True(open.Indices.Length < capped.Indices.Length);
        }

        [Fact]
        public void Cone_Apex_At_Height_And_Base_Ring_At_Y0()
        {
            const float height = 3f;
            const float radius = 0.5f;
            var cone = MeshPrimitives.Cone(radius, height);
            Assert.Equal(0f, cone.Vertices.Min(v => v.Position.Y), 4);
            Assert.Equal(height, cone.Vertices.Max(v => v.Position.Y), 4);

            // base ring verts (y ~ 0) lie within radius
            foreach (var v in cone.Vertices.Where(v => System.MathF.Abs(v.Position.Y) < 1e-3f))
            {
                float r = System.MathF.Sqrt(v.Position.X * v.Position.X + v.Position.Z * v.Position.Z);
                Assert.InRange(r, 0f, radius + 1e-3f);
            }
            // an apex vertex exists at (0,height,0)
            Assert.Contains(cone.Vertices, v =>
                System.MathF.Abs(v.Position.X) < 1e-4f &&
                System.MathF.Abs(v.Position.Z) < 1e-4f &&
                System.MathF.Abs(v.Position.Y - height) < 1e-4f);
        }

        [Fact]
        public void Cone_Uncapped_Has_Fewer_Verts_And_Indices()
        {
            var capped = MeshPrimitives.Cone(capped: true);
            var open = MeshPrimitives.Cone(capped: false);
            Assert.True(open.Vertices.Length < capped.Vertices.Length);
            Assert.True(open.Indices.Length < capped.Indices.Length);
        }

        [Fact]
        public void Pyramid_Base_At_Y0_And_Apex_At_Height()
        {
            const float baseSize = 2f;
            const float height = 1.5f;
            var p = MeshPrimitives.Pyramid(baseSize, height);
            float h = baseSize * 0.5f;
            Assert.Equal(0f, p.Vertices.Min(v => v.Position.Y), 4);
            Assert.Equal(height, p.Vertices.Max(v => v.Position.Y), 4);
            Assert.Contains(p.Vertices, v =>
                System.MathF.Abs(v.Position.X) < 1e-4f &&
                System.MathF.Abs(v.Position.Z) < 1e-4f &&
                System.MathF.Abs(v.Position.Y - height) < 1e-4f);
            // base corners reach +/- h on X and Z
            Assert.Equal(h, p.Vertices.Max(v => v.Position.X), 4);
            Assert.Equal(-h, p.Vertices.Min(v => v.Position.X), 4);
        }

        [Fact]
        public void Wedge_Rises_From_Y0_At_NegZ_To_Height_At_PosZ()
        {
            const float size = 2f;
            const float height = 1f;
            var w = MeshPrimitives.Wedge(size, height);
            float h = size * 0.5f;
            Assert.Equal(0f, w.Vertices.Min(v => v.Position.Y), 4);
            Assert.Equal(height, w.Vertices.Max(v => v.Position.Y), 4);
            // tall verts live at +Z, low verts at -Z
            float maxY = w.Vertices.Max(v => v.Position.Y);
            foreach (var v in w.Vertices.Where(v => System.MathF.Abs(v.Position.Y - maxY) < 1e-3f))
                Assert.Equal(h, v.Position.Z, 3);
            foreach (var v in w.Vertices.Where(v => v.Position.Y < 1e-3f))
                Assert.InRange(v.Position.Z, -h - 1e-3f, h + 1e-3f);
        }

        [Fact]
        public void Sphere_All_Verts_Within_Radius_Of_Origin()
        {
            const float radius = 1.25f;
            var s = MeshPrimitives.Sphere(radius);
            foreach (var v in s.Vertices)
                Assert.Equal(radius, v.Position.Length(), 3);
        }

        [Fact]
        public void Sphere_Normals_Point_Radially_Outward()
        {
            var s = MeshPrimitives.Sphere(0.5f);
            foreach (var v in s.Vertices)
            {
                var expected = Vector3.Normalize(v.Position);
                Assert.True(Vector3.Dot(expected, v.Normal) > 0.99f);
            }
        }

        // --- normal DIRECTION regression: stored normals must point OUTWARD (the renderer uses the
        //     stored normal for shading; an inward normal mis-shades the face). The earlier tests only
        //     checked normal LENGTH, which is why the inward-normal bug on Pyramid/Wedge slipped through. ---

        /// <summary>
        /// For a convex primitive, every triangle's stored per-vertex normal should point away from the mesh
        /// centroid: dot(normal, faceCentroid - meshCentroid) &gt; 0 for each of the triangle's three vertices.
        /// </summary>
        static void AssertAllFaceNormalsOutward(GltfMesh mesh)
        {
            var meshCentroid = Vector3.Zero;
            foreach (var v in mesh.Vertices)
                meshCentroid += v.Position;
            meshCentroid /= mesh.Vertices.Length;

            for (int t = 0; t < mesh.Indices.Length; t += 3)
            {
                ushort i0 = mesh.Indices[t], i1 = mesh.Indices[t + 1], i2 = mesh.Indices[t + 2];
                var p0 = mesh.Vertices[i0].Position;
                var p1 = mesh.Vertices[i1].Position;
                var p2 = mesh.Vertices[i2].Position;
                var faceCentroid = (p0 + p1 + p2) / 3f;
                var outward = faceCentroid - meshCentroid;

                foreach (var vi in new[] { i0, i1, i2 })
                {
                    var n = mesh.Vertices[vi].Normal;
                    Assert.True(Vector3.Dot(n, outward) > 1e-4f,
                        $"Triangle starting at index {t}: vertex {vi} normal {n} points inward " +
                        $"(dot with outward {outward} = {Vector3.Dot(n, outward)}).");
                }
            }
        }

        [Fact]
        public void Box_Face_Normals_Point_Outward()
        {
            // regression guard: Box already stores outward normals; this must pass.
            AssertAllFaceNormalsOutward(MeshPrimitives.Box());
        }

        [Fact]
        public void Pyramid_Face_Normals_Point_Outward()
        {
            AssertAllFaceNormalsOutward(MeshPrimitives.Pyramid());
        }

        [Fact]
        public void Wedge_Face_Normals_Point_Outward()
        {
            AssertAllFaceNormalsOutward(MeshPrimitives.Wedge());
        }

        [Fact]
        public void Cylinder_Cap_Normals_Point_Outward()
        {
            // ±Y cap triangles: dot of the flat cap normal with (faceCentroid - meshCentroid) > 0.
            AssertAllFaceNormalsOutward(MeshPrimitives.Cylinder());
        }

        [Fact]
        public void Cone_Cap_Normals_Point_Outward()
        {
            // the -Y base cap plus the outward/up side normals.
            AssertAllFaceNormalsOutward(MeshPrimitives.Cone());
        }

        [Fact]
        public void Degenerate_Segments_And_Rings_Are_Clamped()
        {
            // segments < 3 clamps to 3, rings < 2 clamps to 2 — no throw, still valid.
            var cyl = MeshPrimitives.Cylinder(segments: 1);
            var cone = MeshPrimitives.Cone(segments: 0);
            var sphere = MeshPrimitives.Sphere(rings: 1, segments: 2);
            foreach (var m in new[] { cyl, cone, sphere })
            {
                Assert.Equal(0, m.Indices.Length % 3);
                Assert.True(m.Vertices.Length > 0);
                foreach (var idx in m.Indices)
                    Assert.InRange(idx, 0, m.Vertices.Length - 1);
            }
        }
    }
}
