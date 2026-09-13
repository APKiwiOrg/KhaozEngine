using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class OutlineNormalBuilderTests
    {
        [Fact]
        public void Faceted_box_corners_share_angle_weighted_normals_independent_of_quad_diagonals()
        {
            GltfMesh first = FacetedBox(flipDiagonals: false);
            GltfMesh second = FacetedBox(flipDiagonals: true);
            ModelVertex[] original = (ModelVertex[])first.Vertices.Clone();

            Vector3[] firstNormals = OutlineNormalBuilder.Build(first.Vertices, first.Indices32);
            Vector3[] secondNormals = OutlineNormalBuilder.Build(second.Vertices, second.Indices32);

            var byPosition = new Dictionary<Vector3, Vector3>();
            for (int i = 0; i < first.Vertices.Length; i++)
            {
                Vector3 position = first.Vertices[i].Position;
                Vector3 expected = Vector3.Normalize(position);
                AssertVector(expected, firstNormals[i]);
                if (byPosition.TryGetValue(position, out Vector3 prior)) AssertVector(prior, firstNormals[i]);
                else byPosition.Add(position, firstNormals[i]);
            }

            for (int i = 0; i < second.Vertices.Length; i++)
                AssertVector(byPosition[second.Vertices[i].Position], secondNormals[i]);

            Assert.Equal(original, first.Vertices);
        }

        [Fact]
        public void Opposite_and_degenerate_faces_get_one_deterministic_finite_fallback_per_position()
        {
            var vertices = new[]
            {
                Vertex(new Vector3(0f, 0f, 0f), Vector3.UnitZ),
                Vertex(new Vector3(1f, 0f, 0f), Vector3.UnitZ),
                Vertex(new Vector3(0f, 1f, 0f), Vector3.UnitZ),
                Vertex(new Vector3(0f, 0f, 0f), -Vector3.UnitZ),
                Vertex(new Vector3(0f, 1f, 0f), -Vector3.UnitZ),
                Vertex(new Vector3(1f, 0f, 0f), -Vector3.UnitZ),
                Vertex(new Vector3(2f, 0f, 0f), Vector3.Zero),
                Vertex(new Vector3(2f, 0f, 0f), new Vector3(float.NaN)),
                Vertex(new Vector3(2f, 0f, 0f), Vector3.Zero),
            };
            uint[] indices = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };

            Vector3[] first = OutlineNormalBuilder.Build(vertices, indices);
            Vector3[] second = OutlineNormalBuilder.Build(vertices, indices);

            for (int i = 0; i < first.Length; i++)
            {
                Assert.True(IsFinite(first[i]), $"outline normal {i} was {first[i]}");
                Assert.InRange(first[i].Length(), 0.9999f, 1.0001f);
                Assert.Equal(first[i], second[i]);
            }
            Assert.Equal(first[0], first[3]);
            Assert.Equal(first[1], first[5]);
            Assert.Equal(first[2], first[4]);
            Assert.Equal(first[6], first[7]);
            Assert.Equal(first[6], first[8]);
        }

        [Fact]
        public void Material_parts_share_corner_normals_across_the_whole_prop()
        {
            GltfMesh box = FacetedBox(flipDiagonals: false);
            var parts = new GltfMeshPart[6];
            for (int face = 0; face < parts.Length; face++)
            {
                var vertices = new ModelVertex[4];
                Array.Copy(box.Vertices, face * 4, vertices, 0, 4);
                parts[face] = new GltfMeshPart(
                    new GltfMesh(vertices, new uint[] { 0, 1, 2, 0, 2, 3 }), default);
            }

            Vector3[][] normals = OutlineNormalBuilder.Build(parts);

            var byPosition = new Dictionary<Vector3, Vector3>();
            for (int part = 0; part < parts.Length; part++)
                for (int vertex = 0; vertex < parts[part].Mesh.Vertices.Length; vertex++)
                {
                    Vector3 position = parts[part].Mesh.Vertices[vertex].Position;
                    Vector3 expected = Vector3.Normalize(position);
                    AssertVector(expected, normals[part][vertex]);
                    if (byPosition.TryGetValue(position, out Vector3 prior))
                        AssertVector(prior, normals[part][vertex]);
                    else
                        byPosition.Add(position, normals[part][vertex]);
                }
        }

        static GltfMesh FacetedBox(bool flipDiagonals)
        {
            Vector3[][] faces =
            {
                new[] { new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(1, 1, 1), new Vector3(1, -1, 1) },
                new[] { new Vector3(-1, -1, 1), new Vector3(-1, 1, 1), new Vector3(-1, 1, -1), new Vector3(-1, -1, -1) },
                new[] { new Vector3(-1, 1, -1), new Vector3(-1, 1, 1), new Vector3(1, 1, 1), new Vector3(1, 1, -1) },
                new[] { new Vector3(-1, -1, 1), new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, -1, 1) },
                new[] { new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1) },
                new[] { new Vector3(1, -1, -1), new Vector3(-1, -1, -1), new Vector3(-1, 1, -1), new Vector3(1, 1, -1) },
            };
            var vertices = new List<ModelVertex>(24);
            var indices = new List<uint>(36);
            foreach (Vector3[] face in faces)
            {
                Vector3 normal = Vector3.Normalize(Vector3.Cross(face[1] - face[0], face[2] - face[0]));
                uint start = (uint)vertices.Count;
                foreach (Vector3 position in face) vertices.Add(Vertex(position, normal));
                if (flipDiagonals)
                    indices.AddRange(new[] { start, start + 1, start + 3, start + 1, start + 2, start + 3 });
                else
                    indices.AddRange(new[] { start, start + 1, start + 2, start, start + 2, start + 3 });
            }
            return new GltfMesh(vertices.ToArray(), indices.ToArray());
        }

        static ModelVertex Vertex(Vector3 position, Vector3 normal)
            => new(position, normal, Vector4.One);

        static bool IsFinite(Vector3 value)
            => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 0.0001f);
            Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 0.0001f);
            Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 0.0001f);
        }
    }
}
