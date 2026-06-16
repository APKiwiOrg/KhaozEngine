using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class MeshBuilderTests
    {
        [Fact]
        public void Add_Offsets_Indices_So_Combined_Indices_Reference_Combined_Vertices()
        {
            var mesh = new MeshBuilder()
                .Add(MeshPrimitives.Box(), Matrix4x4.Identity)
                .Add(MeshPrimitives.Box(), Matrix4x4.CreateTranslation(5f, 0f, 0f))
                .Build();

            foreach (var idx in mesh.Indices)
                Assert.InRange(idx, 0, mesh.Vertices.Length - 1);
            // second part's indices must reach into the appended range
            Assert.True(mesh.Indices.Max() >= 24);
        }

        [Fact]
        public void Two_Adds_Sum_Vertex_And_Index_Counts()
        {
            var a = MeshPrimitives.Box();
            var b = MeshPrimitives.Cylinder();
            var builder = new MeshBuilder()
                .Add(a, Matrix4x4.Identity)
                .Add(b, Matrix4x4.Identity);

            Assert.Equal(a.Vertices.Length + b.Vertices.Length, builder.VertexCount);
            Assert.Equal(a.Indices.Length + b.Indices.Length, builder.IndexCount);

            var built = builder.Build();
            Assert.Equal(builder.VertexCount, built.Vertices.Length);
            Assert.Equal(builder.IndexCount, built.Indices.Length);
        }

        [Fact]
        public void Translated_Box_Has_Positions_Shifted_By_Translation()
        {
            var t = new Vector3(3f, -2f, 7f);
            var src = MeshPrimitives.Box();
            var mesh = new MeshBuilder()
                .Add(src, Matrix4x4.CreateTranslation(t))
                .Build();

            Assert.Equal(src.Vertices.Length, mesh.Vertices.Length);
            for (int i = 0; i < src.Vertices.Length; i++)
            {
                var expected = src.Vertices[i].Position + t;
                Assert.Equal(expected.X, mesh.Vertices[i].Position.X, 4);
                Assert.Equal(expected.Y, mesh.Vertices[i].Position.Y, 4);
                Assert.Equal(expected.Z, mesh.Vertices[i].Position.Z, 4);
            }
        }

        [Fact]
        public void Color_Overload_Sets_Every_Appended_Vertex_Color()
        {
            var color = new Vector4(0.2f, 0.4f, 0.6f, 1f);
            var mesh = new MeshBuilder()
                .Add(MeshPrimitives.Box(), Matrix4x4.Identity)          // white
                .Add(MeshPrimitives.Cylinder(), Matrix4x4.Identity, color)
                .Build();

            int boxVerts = MeshPrimitives.Box().Vertices.Length;
            for (int i = 0; i < boxVerts; i++)
                Assert.Equal(Vector4.One, mesh.Vertices[i].Color);
            for (int i = boxVerts; i < mesh.Vertices.Length; i++)
                Assert.Equal(color, mesh.Vertices[i].Color);
        }

        [Fact]
        public void Add_Keeps_Part_Colors_When_No_Color_Given()
        {
            var mesh = new MeshBuilder()
                .Add(MeshPrimitives.Box(), Matrix4x4.Identity)
                .Build();
            foreach (var v in mesh.Vertices)
                Assert.Equal(Vector4.One, v.Color);
        }

        [Fact]
        public void Add_Preserves_Source_Uvs()
        {
            // a part with distinct per-vertex UVs must keep them through Add (no transform on UV).
            var part = new GltfMesh(new[]
            {
                new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One, new Vector2(0.1f, 0.2f)),
                new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One, new Vector2(0.7f, 0.9f)),
                new ModelVertex(Vector3.UnitZ, Vector3.UnitY, Vector4.One, new Vector2(0.3f, 0.6f)),
            }, new ushort[] { 0, 1, 2 });

            var mesh = new MeshBuilder()
                .Add(part, Matrix4x4.CreateTranslation(5f, 0f, 0f), new Vector4(0.2f, 0.2f, 0.2f, 1f))
                .Build();

            for (int i = 0; i < part.Vertices.Length; i++)
            {
                Assert.Equal(part.Vertices[i].Uv.X, mesh.Vertices[i].Uv.X, 5);
                Assert.Equal(part.Vertices[i].Uv.Y, mesh.Vertices[i].Uv.Y, 5);
            }
        }

        [Fact]
        public void ModelVertex_3Arg_Ctor_Defaults_Uv_To_Zero()
        {
            var v = new ModelVertex(Vector3.One, Vector3.UnitY, Vector4.One);
            Assert.Equal(Vector2.Zero, v.Uv);
        }

        [Fact]
        public void Normals_Stay_Unit_Length_Under_NonUniform_Scale()
        {
            var transform = Matrix4x4.CreateScale(3f, 1f, 0.5f);
            var mesh = new MeshBuilder()
                .Add(MeshPrimitives.Sphere(), transform)
                .Build();
            foreach (var v in mesh.Vertices)
                Assert.Equal(1f, v.Normal.Length(), 3);
        }

        [Fact]
        public void Rotation_Transforms_Normals()
        {
            // 90deg about Z turns +X normal into +Y.
            var rot = Matrix4x4.CreateRotationZ(MathF.PI / 2f);
            var src = MeshPrimitives.Box();
            var mesh = new MeshBuilder().Add(src, rot).Build();

            int plusXFace = src.Vertices.ToList().FindIndex(v =>
                Vector3.Dot(v.Normal, Vector3.UnitX) > 0.99f);
            Assert.True(plusXFace >= 0);
            var n = mesh.Vertices[plusXFace].Normal;
            Assert.True(Vector3.Dot(n, Vector3.UnitY) > 0.99f);
        }

        static GltfMesh FlatPart(int vertexCount)
        {
            var verts = new ModelVertex[vertexCount];
            for (int i = 0; i < vertexCount; i++)
                verts[i] = new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One);
            return new GltfMesh(verts, System.Array.Empty<ushort>());
        }

        [Fact]
        public void Build_Allows_Exactly_65536_Vertices()
        {
            // indices 0..65535 all fit in a ushort, so 65536 vertices is the valid maximum.
            var mesh = new MeshBuilder()
                .Add(FlatPart(ushort.MaxValue + 1), Matrix4x4.Identity)
                .Build();
            Assert.Equal(ushort.MaxValue + 1, mesh.Vertices.Length);
        }

        [Fact]
        public void Build_Throws_When_Exceeding_65536_Vertices()
        {
            // 65537 vertices would need index 65536, which overflows ushort.
            var builder = new MeshBuilder()
                .Add(FlatPart(ushort.MaxValue + 1), Matrix4x4.Identity)
                .Add(FlatPart(1), Matrix4x4.Identity);

            Assert.Equal(ushort.MaxValue + 2, builder.VertexCount);
            Assert.Throws<InvalidOperationException>(() => builder.Build());
        }

        [Fact]
        public void Empty_Build_Yields_Empty_Mesh()
        {
            var mesh = new MeshBuilder().Build();
            Assert.Empty(mesh.Vertices);
            Assert.Empty(mesh.Indices);
        }
    }
}
