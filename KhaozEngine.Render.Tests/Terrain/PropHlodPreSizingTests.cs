using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using Xunit;

namespace KhaozEngine.Tests.Terrain
{
    /// <summary>Issue #393: <see cref="PropHlod.Merge"/> and <see cref="PropHlod.Weld"/> size their output arrays
    /// exactly instead of growing lists and closing with <c>ToArray</c>, which on a real cluster was several times
    /// the final size in transient large-object allocation. That is a pure allocation change, so the whole test is
    /// whether the RESULT moved: these reproduce the pre-#393 implementations verbatim and assert the shipped ones
    /// are byte-for-byte identical to them on a cluster with the properties that could break it (many placements,
    /// several kits, an unknown id to skip, non-trivial yaw and scale, and a weld cell coarse enough to collapse
    /// triangles away).</summary>
    public sealed class PropHlodPreSizingTests
    {
        // --- The pre-#393 implementations, kept verbatim as the reference ----------------------------------------

        static GltfMesh ReferenceMerge(IReadOnlyList<PropPlacement> placements,
                                       IReadOnlyDictionary<string, GltfMesh> sourceMeshes)
        {
            var verts = new List<ModelVertex>();
            var idx = new List<uint>();
            for (int p = 0; p < placements.Count; p++)
            {
                PropPlacement pl = placements[p];
                if (!sourceMeshes.TryGetValue(pl.Id, out GltfMesh? mesh) || mesh == null) continue;

                Matrix4x4 rot = Matrix4x4.CreateRotationY(pl.Yaw);
                Matrix4x4 world = Matrix4x4.CreateScale(pl.Scale) * rot * Matrix4x4.CreateTranslation(pl.X, pl.Y, pl.Z);
                uint baseIndex = (uint)verts.Count;
                ModelVertex[] mv = mesh.Vertices;
                for (int i = 0; i < mv.Length; i++)
                {
                    ModelVertex v = mv[i];
                    v.Position = Vector3.Transform(mv[i].Position, world);
                    v.Normal = Vector3.Normalize(Vector3.TransformNormal(mv[i].Normal, rot));
                    v.Tangent = Vector4.Zero;
                    verts.Add(v);
                }
                uint[] mi = mesh.Indices32;
                for (int i = 0; i < mi.Length; i++) idx.Add(baseIndex + mi[i]);
            }
            return new GltfMesh(verts.ToArray(), idx.ToArray());
        }

        static GltfMesh ReferenceWeld(GltfMesh mesh, float cellSize)
        {
            var cellOf = new Dictionary<(int, int, int), int>();
            var accPos = new List<Vector3>();
            var accNrm = new List<Vector3>();
            var accCol = new List<Vector4>();
            var accCnt = new List<int>();
            var remap = new int[mesh.Vertices.Length];
            for (int i = 0; i < mesh.Vertices.Length; i++)
            {
                ModelVertex v = mesh.Vertices[i];
                var key = ((int)MathF.Floor(v.Position.X / cellSize),
                           (int)MathF.Floor(v.Position.Y / cellSize),
                           (int)MathF.Floor(v.Position.Z / cellSize));
                if (!cellOf.TryGetValue(key, out int id))
                {
                    id = accPos.Count;
                    cellOf[key] = id;
                    accPos.Add(Vector3.Zero); accNrm.Add(Vector3.Zero); accCol.Add(Vector4.Zero); accCnt.Add(0);
                }
                accPos[id] += v.Position; accNrm[id] += v.Normal; accCol[id] += v.Color; accCnt[id]++;
                remap[i] = id;
            }

            var outV = new ModelVertex[accPos.Count];
            for (int i = 0; i < outV.Length; i++)
            {
                float inv = 1f / accCnt[i];
                Vector3 n = accNrm[i] * inv;
                outV[i] = new ModelVertex(accPos[i] * inv,
                    n.LengthSquared() > 1e-8f ? Vector3.Normalize(n) : Vector3.UnitY,
                    accCol[i] * inv);
            }

            uint[] si = mesh.Indices32;
            var outI = new List<uint>();
            for (int t = 0; t + 2 < si.Length; t += 3)
            {
                int a = remap[si[t]], b = remap[si[t + 1]], c = remap[si[t + 2]];
                if (a == b || b == c || a == c) continue;
                outI.Add((uint)a); outI.Add((uint)b); outI.Add((uint)c);
            }
            return new GltfMesh(outV, outI.ToArray());
        }

        // --- The fixture cluster ---------------------------------------------------------------------------------

        static IReadOnlyDictionary<string, GltfMesh> Kit() => new Dictionary<string, GltfMesh>
        {
            ["pine"] = MeshPrimitives.Sphere(radius: 1.4f, rings: 9, segments: 13),
            ["rock"] = MeshPrimitives.Sphere(radius: 0.9f, rings: 6, segments: 8),
        };

        // 60 placements over a 40 m square, alternating kits, with one id the kit has never heard of so the
        // skip-an-unknown-id branch is exercised on both sides of the comparison.
        static List<PropPlacement> Cluster()
        {
            var list = new List<PropPlacement>();
            for (int i = 0; i < 60; i++)
            {
                string id = i % 7 == 0 ? "unknown_kit" : (i % 2 == 0 ? "pine" : "rock");
                float x = (i * 7 % 40) + 0.37f * i;
                float z = (i * 13 % 40) - 0.21f * i;
                float y = 3f + 0.05f * i;
                list.Add(new PropPlacement(id, x, y, z, 0.8f + 0.03f * i, 0.11f * i, i % 3));
            }
            return list;
        }

        static void AssertByteIdentical(GltfMesh expected, GltfMesh got)
        {
            Assert.Equal(expected.Vertices.Length, got.Vertices.Length);
            Assert.Equal(expected.Indices32.Length, got.Indices32.Length);
            for (int i = 0; i < expected.Vertices.Length; i++)
            {
                ModelVertex a = expected.Vertices[i], b = got.Vertices[i];
                Assert.Equal(a.Position, b.Position);
                Assert.Equal(a.Normal, b.Normal);
                Assert.Equal(a.Color, b.Color);
                Assert.Equal(a.Uv, b.Uv);
                Assert.Equal(a.Tangent, b.Tangent);
            }
            for (int i = 0; i < expected.Indices32.Length; i++) Assert.Equal(expected.Indices32[i], got.Indices32[i]);
        }

        // --- The comparisons -------------------------------------------------------------------------------------

        [Fact]
        public void Merge_IsByteIdenticalToThePreSizedReference()
        {
            List<PropPlacement> cluster = Cluster();
            IReadOnlyDictionary<string, GltfMesh> kit = Kit();

            GltfMesh expected = ReferenceMerge(cluster, kit);
            Assert.True(expected.Vertices.Length > 4000);      // not vacuous: a real large-object-sized merge

            AssertByteIdentical(expected, PropHlod.Merge(cluster, kit));
        }

        [Theory]
        [InlineData(0.4f)]    // fine cell: little collapses, most triangles survive
        [InlineData(1.5f)]    // the measured production cell
        [InlineData(6f)]      // coarse: whole props collapse to a handful of cells, most triangles degenerate away
        public void Weld_IsByteIdenticalToThePreSizedReference(float cell)
        {
            GltfMesh merged = ReferenceMerge(Cluster(), Kit());

            GltfMesh expected = ReferenceWeld(merged, cell);
            Assert.True(expected.Vertices.Length < merged.Vertices.Length);   // not vacuous: the weld really reduced
            Assert.True(expected.Indices32.Length < merged.Indices32.Length); // and really dropped degenerate triangles

            AssertByteIdentical(expected, PropHlod.Weld(merged, cell));
        }

        [Fact]
        public void BuildMergedMesh_IsByteIdenticalToThePreSizedReference()
        {
            List<PropPlacement> cluster = Cluster();
            IReadOnlyDictionary<string, GltfMesh> kit = Kit();

            GltfMesh expected = ReferenceWeld(ReferenceMerge(cluster, kit), 1.5f);

            AssertByteIdentical(expected, PropHlod.BuildMergedMesh(cluster, kit, 1.5f));
        }

        [Fact]
        public void Merge_OfAnAllUnknownCluster_IsStillAnEmptyMesh()
        {
            // The counting pass and the fill pass must agree on "skip this one", so the degenerate end of that
            // agreement is worth pinning: zero counted, zero written, no exception from a zero-length array.
            var cluster = new List<PropPlacement> { new("nope", 1f, 2f, 3f, 1f, 0.5f, 0) };

            GltfMesh merged = PropHlod.Merge(cluster, Kit());

            Assert.Empty(merged.Vertices);
            Assert.Empty(merged.Indices32);
            Assert.Equal(0, merged.TriangleCount);
        }
    }
}
