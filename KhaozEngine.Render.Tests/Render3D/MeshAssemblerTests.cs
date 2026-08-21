using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Welding rules for the mesh assembler that backs <see cref="GltfLoader"/>. Covers the bugs the old
    /// position-only weld had: it merged UV seams and hard edges. (Large-mesh / 32-bit-index behaviour, which
    /// replaced the old ushort-ceiling throw, lives in <see cref="Uint32IndexTests"/>.)
    /// </summary>
    public class MeshAssemblerTests
    {
        static MeshCorner C(Vector3 p, Vector3? n, Vector2 uv) => new MeshCorner(p, n, Vector4.One, uv);

        // A unit CCW triangle in the XY plane (normal should face +Z).
        static readonly Vector3 A = new(0, 0, 0), B = new(1, 0, 0), Cv = new(0, 1, 0);

        [Fact]
        public void Identical_Corners_Weld_To_One_Vertex()
        {
            var tris = new List<MeshCorner>
            {
                C(A, null, Vector2.Zero), C(B, null, Vector2.Zero), C(Cv, null, Vector2.Zero),
                C(A, null, Vector2.Zero), C(B, null, Vector2.Zero), C(Cv, null, Vector2.Zero),
            };
            var mesh = MeshAssembler.Build(tris);
            Assert.Equal(3, mesh.Vertices.Length);   // the duplicate triangle adds no new verts
            Assert.Equal(6, mesh.Indices.Length);
        }

        [Fact]
        public void Same_Position_Different_Uv_Stays_Two_Vertices()
        {
            // UV seam: the shared corner A appears with two different UVs and must NOT be welded.
            var tris = new List<MeshCorner>
            {
                C(A, Vector3.UnitZ, new Vector2(0f, 0f)), C(B, Vector3.UnitZ, Vector2.Zero), C(Cv, Vector3.UnitZ, Vector2.Zero),
                C(A, Vector3.UnitZ, new Vector2(0.9f, 0.9f)), C(B, Vector3.UnitZ, Vector2.Zero), C(Cv, Vector3.UnitZ, Vector2.Zero),
            };
            var mesh = MeshAssembler.Build(tris);
            int atA = 0;
            foreach (var v in mesh.Vertices) if (v.Position == A) atA++;
            Assert.Equal(2, atA);
        }

        [Fact]
        public void Same_Position_Different_Source_Normal_Stays_Two_Vertices()
        {
            // Hard edge: the corner shares a position but carries two different source normals.
            var tris = new List<MeshCorner>
            {
                C(A, Vector3.UnitZ, Vector2.Zero), C(B, Vector3.UnitZ, Vector2.Zero), C(Cv, Vector3.UnitZ, Vector2.Zero),
                C(A, Vector3.UnitX, Vector2.Zero), C(B, Vector3.UnitX, Vector2.Zero), C(Cv, Vector3.UnitX, Vector2.Zero),
            };
            var mesh = MeshAssembler.Build(tris);
            int atA = 0;
            foreach (var v in mesh.Vertices) if (v.Position == A) atA++;
            Assert.Equal(2, atA);
        }

        [Fact]
        public void No_Source_Normal_Computes_Outward_Face_Normal_From_Winding()
        {
            // CCW winding in XY -> cross((B-A),(C-A)) points +Z. The computed normal must follow it.
            var mesh = MeshAssembler.Build(new List<MeshCorner> { C(A, null, Vector2.Zero), C(B, null, Vector2.Zero), C(Cv, null, Vector2.Zero) });
            Assert.Equal(3, mesh.Vertices.Length);
            foreach (var v in mesh.Vertices)
            {
                Assert.Equal(1f, v.Normal.Length(), 3);
                Assert.True(Vector3.Dot(v.Normal, Vector3.UnitZ) > 0.99f);
            }
        }

        [Fact]
        public void Source_Normal_Is_Preserved()
        {
            var n = Vector3.Normalize(new Vector3(1, 2, 3));
            var mesh = MeshAssembler.Build(new List<MeshCorner> { C(A, n, Vector2.Zero), C(B, n, Vector2.Zero), C(Cv, n, Vector2.Zero) });
            foreach (var v in mesh.Vertices)
            {
                Assert.Equal(n.X, v.Normal.X, 3);
                Assert.Equal(n.Y, v.Normal.Y, 3);
                Assert.Equal(n.Z, v.Normal.Z, 3);
            }
        }

        [Fact]
        public void Indices_Reference_Valid_Vertices()
        {
            var mesh = MeshAssembler.Build(new List<MeshCorner> { C(A, null, Vector2.Zero), C(B, null, Vector2.Zero), C(Cv, null, Vector2.Zero) });
            foreach (var i in mesh.Indices) Assert.InRange(i, 0, mesh.Vertices.Length - 1);
        }

        [Fact]
        public void Empty_Input_Yields_Empty_Mesh()
        {
            var mesh = MeshAssembler.Build(Array.Empty<MeshCorner>());
            Assert.Empty(mesh.Vertices);
            Assert.Empty(mesh.Indices);
        }

        [Fact]
        public void Throws_On_NonMultiple_Of_Three()
        {
            Assert.Throws<ArgumentException>(() =>
                MeshAssembler.Build(new List<MeshCorner> { C(A, null, Vector2.Zero), C(B, null, Vector2.Zero) }));
        }

        // Two coplanar triangles sharing the edge B-Cv, no normals and no UVs: the shape a flat-shaded kit piece
        // makes wherever two palette shades meet. The colour has to be in the weld key or the second triangle's
        // two shared corners come back in the first triangle's colour.
        static readonly Vector3 D = new(1, 1, 0);

        static MeshCorner Painted(Vector3 p, Vector4 color) =>
            new MeshCorner(p, null, color, Vector2.Zero, null, hasVertexColor: true);

        static MeshCorner Flat(Vector3 p, Vector4 color) => new MeshCorner(p, null, color, Vector2.Zero);

        static List<MeshCorner> Seam(Func<Vector3, Vector4, MeshCorner> make)
        {
            Vector4 red = new(1, 0, 0, 1), blue = new(0, 0, 1, 1);
            return new List<MeshCorner>
            {
                make(A, red), make(B, red), make(Cv, red),
                make(B, blue), make(D, blue), make(Cv, blue),
            };
        }

        [Fact]
        public void Per_Vertex_Colour_Seam_Stays_Split()
        {
            var mesh = MeshAssembler.Build(Seam(Painted));

            // Four distinct positions, but B and Cv each carry two colours, so six vertices.
            Assert.Equal(6, mesh.Vertices.Length);
            for (int t = 0; t < 2; t++)
            {
                Vector4 expected = mesh.Vertices[mesh.Indices[t * 3]].Color;
                for (int i = 1; i < 3; i++)
                    Assert.Equal(expected, mesh.Vertices[mesh.Indices[t * 3 + i]].Color);
            }
            Assert.NotEqual(mesh.Vertices[mesh.Indices[0]].Color, mesh.Vertices[mesh.Indices[3]].Color);
        }

        [Fact]
        public void A_Flat_Colour_Difference_Does_Not_Split_The_Weld()
        {
            // The same six corners with the flag off, which is every non-COLOR_0 caller: the colour stays out of
            // the key, the shared edge welds, and the first corner to claim a vertex keeps its colour, exactly as
            // before per-vertex colour existed. This is the guarantee the goldens rest on.
            var mesh = MeshAssembler.Build(Seam(Flat));

            Assert.Equal(4, mesh.Vertices.Length);
            Assert.Equal(6, mesh.Indices.Length);
        }

        // Hash quality, pinned without timing anything. A ValueTuple past seven elements hashes only its
        // seventh element and its Rest, so the 14-lane key the weld briefly used dropped all three position
        // lanes out of the hash: an unmapped mesh piled every vertex into one bucket and the weld went
        // quadratic (bell_tower.glb took 672 ms instead of 12). Distinct positions, everything else identical,
        // which is exactly a palette-painted kit piece with no UVs.
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void The_Weld_Key_Hashes_Every_Position_Lane(int axis)
        {
            const int n = 4096;
            var hashes = new HashSet<int>();
            for (int i = 0; i < n; i++)
            {
                var p = Vector3.Zero;
                if (axis == 0) p.X = i * 0.25f;
                else if (axis == 1) p.Y = i * 0.25f;
                else p.Z = i * 0.25f;
                var corner = new MeshCorner(p, Vector3.UnitY, Vector4.One, Vector2.Zero);
                hashes.Add(MeshWeldKey.From(corner).GetHashCode());
            }

            Assert.True(hashes.Count >= n / 2,
                $"position lane {axis} is out of the hash: only {hashes.Count} distinct hash codes for {n} distinct positions");
        }
    }
}
