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
                MeshPrimitives.Plane(), MeshPrimitives.RoundedBox(),
                MeshPrimitives.Capsule(), MeshPrimitives.Torus(),
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
            yield return new object[] { MeshPrimitives.Plane() };
            yield return new object[] { MeshPrimitives.Plane(2f, 3f, 4, 5) };
            yield return new object[] { MeshPrimitives.RoundedBox() };
            yield return new object[] { MeshPrimitives.Capsule() };
            yield return new object[] { MeshPrimitives.Torus() };
        }

        // every primitive carries a UV channel; for the bounded mappings (all of them) UVs stay in [0,1].
        public static System.Collections.Generic.IEnumerable<object[]> AllPrimitives()
        {
            yield return new object[] { MeshPrimitives.Box() };
            yield return new object[] { MeshPrimitives.Tile() };
            yield return new object[] { MeshPrimitives.Cylinder() };
            yield return new object[] { MeshPrimitives.Cone() };
            yield return new object[] { MeshPrimitives.Pyramid() };
            yield return new object[] { MeshPrimitives.Wedge() };
            yield return new object[] { MeshPrimitives.Sphere() };
            yield return new object[] { MeshPrimitives.Plane(2f, 3f, 4, 5) };
            yield return new object[] { MeshPrimitives.RoundedBox() };
            yield return new object[] { MeshPrimitives.Capsule() };
            yield return new object[] { MeshPrimitives.Torus() };
        }

        [Theory]
        [MemberData(nameof(AllPrimitives))]
        public void Primitive_Uvs_Are_Within_Unit_Square(GltfMesh mesh)
        {
            foreach (var v in mesh.Vertices)
            {
                Assert.InRange(v.Uv.X, -1e-4f, 1f + 1e-4f);
                Assert.InRange(v.Uv.Y, -1e-4f, 1f + 1e-4f);
            }
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

        // --- new primitives (round 2) ---

        [Fact]
        public void Plane_Is_Flat_At_Y0_Centered_With_PlusY_Normals()
        {
            const float w = 4f, d = 2f;
            var plane = MeshPrimitives.Plane(w, d, 3, 2);
            foreach (var v in plane.Vertices)
            {
                Assert.Equal(0f, v.Position.Y, 4);
                Assert.True(Vector3.Dot(v.Normal, Vector3.UnitY) > 0.99f);
            }
            Assert.Equal(w / 2f, plane.Vertices.Max(v => v.Position.X), 4);
            Assert.Equal(-w / 2f, plane.Vertices.Min(v => v.Position.X), 4);
            Assert.Equal(d / 2f, plane.Vertices.Max(v => v.Position.Z), 4);
            Assert.Equal(-d / 2f, plane.Vertices.Min(v => v.Position.Z), 4);
            // UV spans the full unit square.
            Assert.Equal(0f, plane.Vertices.Min(v => v.Uv.X), 4);
            Assert.Equal(1f, plane.Vertices.Max(v => v.Uv.X), 4);
        }

        [Fact]
        public void Plane_Subdivision_Increases_Vertex_Count()
        {
            var coarse = MeshPrimitives.Plane(1f, 1f, 1, 1);
            var fine = MeshPrimitives.Plane(1f, 1f, 4, 4);
            Assert.Equal(4, coarse.Vertices.Length);   // 2x2 grid
            Assert.Equal(25, fine.Vertices.Length);     // 5x5 grid
            Assert.True(fine.Indices.Length > coarse.Indices.Length);
        }

        [Fact]
        public void Plane_Face_Normals_Point_Up()
        {
            var plane = MeshPrimitives.Plane(2f, 2f, 2, 2);
            for (int t = 0; t < plane.Indices.Length; t += 3)
            {
                var p0 = plane.Vertices[plane.Indices[t]].Position;
                var p1 = plane.Vertices[plane.Indices[t + 1]].Position;
                var p2 = plane.Vertices[plane.Indices[t + 2]].Position;
                var faceN = Vector3.Cross(p1 - p0, p2 - p0);
                Assert.True(Vector3.Dot(faceN, Vector3.UnitY) > 0f,
                    $"Plane triangle at index {t} winds away from +Y.");
            }
        }

        [Fact]
        public void RoundedBox_Stays_Within_Half_Size_And_Has_Unit_Normals()
        {
            const float size = 2f, radius = 0.3f;
            var rb = MeshPrimitives.RoundedBox(size, radius, 4);
            float h = size * 0.5f;
            foreach (var v in rb.Vertices)
            {
                Assert.InRange(v.Position.X, -h - 1e-3f, h + 1e-3f);
                Assert.InRange(v.Position.Y, -h - 1e-3f, h + 1e-3f);
                Assert.InRange(v.Position.Z, -h - 1e-3f, h + 1e-3f);
                Assert.Equal(1f, v.Normal.Length(), 3);
            }
            // corners are rounded: no vertex sits at the sharp cube corner (h,h,h).
            Assert.DoesNotContain(rb.Vertices, v =>
                System.MathF.Abs(v.Position.X - h) < 1e-3f &&
                System.MathF.Abs(v.Position.Y - h) < 1e-3f &&
                System.MathF.Abs(v.Position.Z - h) < 1e-3f);
        }

        [Fact]
        public void RoundedBox_Clamps_Oversized_Radius()
        {
            // radius >= size/2 must clamp; no throw, valid mesh, still bounded by half-size.
            var rb = MeshPrimitives.RoundedBox(1f, 5f, 3);
            Assert.Equal(0, rb.Indices.Length % 3);
            Assert.True(rb.Vertices.Length > 0);
            foreach (var v in rb.Vertices)
                Assert.InRange(v.Position.Length(), 0f, 0.5f * System.MathF.Sqrt(3f) + 1e-3f);
        }

        [Fact]
        public void Capsule_Base_At_Y0_And_Total_Height_Is_Height_Plus_TwoRadius()
        {
            const float radius = 0.5f, height = 2f;
            var cap = MeshPrimitives.Capsule(radius, height);
            Assert.Equal(0f, cap.Vertices.Min(v => v.Position.Y), 3);
            Assert.Equal(height + 2f * radius, cap.Vertices.Max(v => v.Position.Y), 3);
            // radial extent never exceeds the radius.
            foreach (var v in cap.Vertices)
            {
                float r = System.MathF.Sqrt(v.Position.X * v.Position.X + v.Position.Z * v.Position.Z);
                Assert.InRange(r, 0f, radius + 1e-3f);
                Assert.Equal(1f, v.Normal.Length(), 3);
            }
        }

        [Fact]
        public void Capsule_Normals_Point_Outward_From_Axis()
        {
            // every side/cap normal has a non-negative outward radial component along its own position.
            var cap = MeshPrimitives.Capsule(0.5f, 1f, 12, 4);
            foreach (var v in cap.Vertices)
            {
                var radial = new Vector3(v.Position.X, 0f, v.Position.Z);
                if (radial.LengthSquared() < 1e-6f) continue; // pole
                Assert.True(Vector3.Dot(Vector3.Normalize(radial), v.Normal) > -1e-3f);
            }
        }

        [Fact]
        public void Torus_Is_Centered_With_Correct_Radial_Extent()
        {
            const float major = 1f, minor = 0.25f;
            var torus = MeshPrimitives.Torus(major, minor);
            foreach (var v in torus.Vertices)
            {
                float r = System.MathF.Sqrt(v.Position.X * v.Position.X + v.Position.Z * v.Position.Z);
                Assert.InRange(r, major - minor - 1e-3f, major + minor + 1e-3f);
                Assert.InRange(v.Position.Y, -minor - 1e-3f, minor + 1e-3f);
                Assert.Equal(1f, v.Normal.Length(), 3);
            }
            // symmetric about the origin in XZ: the bounding box is centered on 0.
            Assert.Equal(0f, (torus.Vertices.Max(v => v.Position.X) + torus.Vertices.Min(v => v.Position.X)) * 0.5f, 3);
            Assert.Equal(0f, (torus.Vertices.Max(v => v.Position.Z) + torus.Vertices.Min(v => v.Position.Z)) * 0.5f, 3);
        }

        [Fact]
        public void Torus_Normals_Point_Outward_From_Tube_Center()
        {
            const float major = 0.5f, minor = 0.2f;
            var torus = MeshPrimitives.Torus(major, minor, 12, 8);
            foreach (var v in torus.Vertices)
            {
                // the tube-cross-section centre for this vertex lies on the major circle.
                var radial = new Vector3(v.Position.X, 0f, v.Position.Z);
                var tubeCenter = radial.LengthSquared() > 1e-6f
                    ? Vector3.Normalize(radial) * major
                    : Vector3.Zero;
                var outward = v.Position - tubeCenter;
                Assert.True(Vector3.Dot(Vector3.Normalize(outward), v.Normal) > 0.9f);
            }
        }

        [Fact]
        public void New_Shapes_Clamp_Degenerate_Args()
        {
            var plane = MeshPrimitives.Plane(1f, 1f, 0, -3);
            var rb = MeshPrimitives.RoundedBox(1f, 0.2f, 0);
            var cap = MeshPrimitives.Capsule(0.5f, 1f, 1, 0);
            var torus = MeshPrimitives.Torus(0.5f, 0.2f, 2, 1);
            foreach (var m in new[] { plane, rb, cap, torus })
            {
                Assert.Equal(0, m.Indices.Length % 3);
                Assert.True(m.Vertices.Length > 0);
                foreach (var idx in m.Indices)
                    Assert.InRange(idx, 0, m.Vertices.Length - 1);
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
        public void RoundedBox_Face_Normals_Point_Outward()
        {
            // convex solid: every stored normal points away from the centroid (origin).
            AssertAllFaceNormalsOutward(MeshPrimitives.RoundedBox());
        }

        [Fact]
        public void Torus_Face_Normals_Have_Consistent_Winding()
        {
            // not centroid-convex, so check triangle winding agrees with the stored (outward) normals instead.
            var torus = MeshPrimitives.Torus();
            for (int t = 0; t < torus.Indices.Length; t += 3)
            {
                ushort i0 = torus.Indices[t], i1 = torus.Indices[t + 1], i2 = torus.Indices[t + 2];
                var p0 = torus.Vertices[i0].Position;
                var p1 = torus.Vertices[i1].Position;
                var p2 = torus.Vertices[i2].Position;
                var faceN = Vector3.Cross(p1 - p0, p2 - p0);
                var avgStored = torus.Vertices[i0].Normal + torus.Vertices[i1].Normal + torus.Vertices[i2].Normal;
                Assert.True(Vector3.Dot(faceN, avgStored) > 0f,
                    $"Torus triangle at index {t} winds opposite its outward normal.");
            }
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
