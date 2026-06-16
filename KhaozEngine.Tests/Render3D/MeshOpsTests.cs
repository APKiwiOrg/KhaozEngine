using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshOpsTests
    {
        [Fact]
        public void WithSmoothNormals_Averages_Normals_At_Shared_Corner()
        {
            // a flat-shaded box has 3 vertices per geometric corner (one per face), each with a face normal
            // (±X / ±Y / ±Z). After welding by position, every corner's normal becomes the averaged direction
            // of its three faces, pointing diagonally outward toward that corner.
            var box = MeshPrimitives.Box(2f);
            var smooth = MeshOps.WithSmoothNormals(box);

            Assert.Equal(box.Vertices.Length, smooth.Vertices.Length);
            Assert.Equal(box.Indices.Length, smooth.Indices.Length);

            foreach (var v in smooth.Vertices)
            {
                Assert.Equal(1f, v.Normal.Length(), 4); // re-normalized
                // corner at (±1,±1,±1): the averaged normal should point along that diagonal.
                var expected = Vector3.Normalize(v.Position);
                Assert.True(Vector3.Dot(expected, v.Normal) > 0.99f,
                    $"Vertex {v.Position} smoothed normal {v.Normal} is not the corner diagonal.");
            }
        }

        [Fact]
        public void WithSmoothNormals_Leaves_Positions_Colors_Uvs_Indices_Intact()
        {
            var box = MeshPrimitives.Box();
            var smooth = MeshOps.WithSmoothNormals(box);

            Assert.Equal(box.Indices, smooth.Indices);
            for (int i = 0; i < box.Vertices.Length; i++)
            {
                Assert.Equal(box.Vertices[i].Position, smooth.Vertices[i].Position);
                Assert.Equal(box.Vertices[i].Color, smooth.Vertices[i].Color);
                Assert.Equal(box.Vertices[i].Uv, smooth.Vertices[i].Uv);
            }
        }

        [Fact]
        public void WithSmoothNormals_Does_Not_Mutate_Input()
        {
            var box = MeshPrimitives.Box();
            var before = box.Vertices.Select(v => v.Normal).ToArray();
            _ = MeshOps.WithSmoothNormals(box);
            for (int i = 0; i < box.Vertices.Length; i++)
                Assert.Equal(before[i], box.Vertices[i].Normal);
        }

        [Fact]
        public void WithSmoothNormals_Welds_Two_Coincident_Vertices()
        {
            // two vertices at the same position with opposite normals -> both become ~zero-averaged, so the
            // helper falls back to keeping the original normal (no NaN, still unit-ish).
            var verts = new[]
            {
                new ModelVertex(Vector3.Zero, Vector3.UnitX, Vector4.One, Vector2.Zero),
                new ModelVertex(Vector3.Zero, -Vector3.UnitX, Vector4.One, Vector2.Zero),
                new ModelVertex(Vector3.UnitY, Vector3.UnitY, Vector4.One, Vector2.Zero),
            };
            var mesh = new GltfMesh(verts, new ushort[] { 0, 1, 2 });
            var smooth = MeshOps.WithSmoothNormals(mesh);

            foreach (var v in smooth.Vertices)
                Assert.False(float.IsNaN(v.Normal.X) || float.IsNaN(v.Normal.Y) || float.IsNaN(v.Normal.Z));
            // distinct position keeps its own normal.
            Assert.True(Vector3.Dot(smooth.Vertices[2].Normal, Vector3.UnitY) > 0.99f);
        }

        [Fact]
        public void RecomputeFlatNormals_Gives_Each_Triangle_Its_Face_Normal()
        {
            // a single CCW triangle in the XZ plane (seen from +Y) gets a +Y face normal on all three verts.
            var verts = new[]
            {
                new ModelVertex(new Vector3(0, 0, 0), Vector3.Zero, Vector4.One, Vector2.Zero),
                new ModelVertex(new Vector3(0, 0, 1), Vector3.Zero, Vector4.One, Vector2.Zero),
                new ModelVertex(new Vector3(1, 0, 0), Vector3.Zero, Vector4.One, Vector2.Zero),
            };
            var mesh = new GltfMesh(verts, new ushort[] { 0, 1, 2 });
            var flat = MeshOps.RecomputeFlatNormals(mesh);
            foreach (var v in flat.Vertices)
                Assert.True(Vector3.Dot(v.Normal, Vector3.UnitY) > 0.99f);
        }

        [Fact]
        public void WithSmoothNormals_Throws_On_Null()
        {
            Assert.Throws<ArgumentNullException>(() => MeshOps.WithSmoothNormals(null!));
        }
    }
}
