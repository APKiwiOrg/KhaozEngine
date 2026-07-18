using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// 32-bit (uint) mesh index support. Meshes past the 65,536-vertex ushort ceiling load with
    /// <see cref="GpuIndexFormat.UInt32"/>; small meshes keep selecting <see cref="GpuIndexFormat.UInt16"/> so
    /// their index buffers (and therefore the rendered output) stay byte-identical. The legacy
    /// <see cref="GltfMesh.Indices"/> 16-bit view still works for fitting meshes and throws for 32-bit ones.
    /// </summary>
    public class Uint32IndexTests
    {
        // N distinct verts (no weld), indices 0..N-1, built through the uint constructor.
        static GltfMesh FlatMesh32(int vertexCount)
        {
            var verts = new ModelVertex[vertexCount];
            var idx = new uint[vertexCount];
            for (int i = 0; i < vertexCount; i++)
            {
                verts[i] = new ModelVertex(new Vector3(i, 0, 0), Vector3.UnitY, Vector4.One);
                idx[i] = (uint)i;
            }
            return new GltfMesh(verts, idx);
        }

        // A flat part with n verts and indices 0..n-1, built through the ushort constructor (n <= 65536).
        static GltfMesh IndexedFlatPart(int n)
        {
            var verts = new ModelVertex[n];
            var idx = new ushort[n];
            for (int i = 0; i < n; i++)
            {
                verts[i] = new ModelVertex(new Vector3(i, 0, 0), Vector3.UnitY, Vector4.One);
                idx[i] = (ushort)i;
            }
            return new GltfMesh(verts, idx);
        }

        [Fact]
        public void Small_Mesh_Selects_UInt16()
        {
            var box = MeshPrimitives.Box();
            Assert.Equal(GpuIndexFormat.UInt16, box.IndexFormat);
            Assert.NotEmpty(box.Indices);                       // 16-bit view still available
            Assert.Equal(box.Indices32.Length, box.Indices.Length);
            for (int i = 0; i < box.Indices.Length; i++)
                Assert.Equal((uint)box.Indices[i], box.Indices32[i]);
        }

        [Fact]
        public void Exactly_65536_Vertices_Stays_UInt16()
        {
            // max index is 65535, which fits a ushort, so this is still a 16-bit mesh.
            var mesh = IndexedFlatPart(ushort.MaxValue + 1);
            Assert.Equal(GpuIndexFormat.UInt16, mesh.IndexFormat);
            Assert.Equal(ushort.MaxValue + 1, mesh.Indices.Length);
        }

        [Fact]
        public void Mesh_Past_65k_Vertices_Selects_UInt32()
        {
            var mesh = FlatMesh32(ushort.MaxValue + 2);         // 65537 verts, max index 65536
            Assert.Equal(GpuIndexFormat.UInt32, mesh.IndexFormat);
            Assert.Equal(ushort.MaxValue + 2, mesh.Vertices.Length);
            Assert.Equal((uint)(ushort.MaxValue + 1), mesh.Indices32.Max());
        }

        [Fact]
        public void Indices_Property_Throws_On_A_32Bit_Mesh()
        {
            var mesh = FlatMesh32(ushort.MaxValue + 2);
            Assert.Throws<InvalidOperationException>(() => { _ = mesh.Indices; });
        }

        [Fact]
        public void MeshAssembler_Past_The_Ushort_Ceiling_Loads_With_UInt32()
        {
            // 65537+ distinct (un-welded) corners. The old assembler threw / truncated; now it builds a
            // 32-bit-indexed mesh end to end.
            var corners = new List<MeshCorner>();
            int made = 0;
            while (made <= ushort.MaxValue + 1)
            {
                corners.Add(new MeshCorner(new Vector3(made++, 0, 0), Vector3.UnitZ, Vector4.One, Vector2.Zero));
                corners.Add(new MeshCorner(new Vector3(made++, 1, 0), Vector3.UnitZ, Vector4.One, Vector2.Zero));
                corners.Add(new MeshCorner(new Vector3(made++, 0, 1), Vector3.UnitZ, Vector4.One, Vector2.Zero));
            }
            var mesh = MeshAssembler.Build(corners);
            Assert.True(mesh.Vertices.Length > ushort.MaxValue + 1);
            Assert.Equal(GpuIndexFormat.UInt32, mesh.IndexFormat);
            Assert.All(mesh.Indices32, i => Assert.InRange(i, 0u, (uint)(mesh.Vertices.Length - 1)));
        }

        [Fact]
        public void MeshBuilder_Small_Stays_UInt16()
        {
            var mesh = new MeshBuilder().Add(MeshPrimitives.Box(), Matrix4x4.Identity).Build();
            Assert.Equal(GpuIndexFormat.UInt16, mesh.IndexFormat);
        }

        [Fact]
        public void MeshBuilder_Fuses_Across_The_65536_Boundary_With_UInt32()
        {
            var builder = new MeshBuilder()
                .Add(IndexedFlatPart(ushort.MaxValue + 1), Matrix4x4.Identity)   // 65536 verts, idx 0..65535
                .Add(IndexedFlatPart(2), Matrix4x4.Identity);                    // +2 verts -> idx 65536, 65537

            Assert.Equal(ushort.MaxValue + 3, builder.VertexCount);
            var mesh = builder.Build();
            Assert.Equal(GpuIndexFormat.UInt32, mesh.IndexFormat);
            Assert.Equal(ushort.MaxValue + 3, mesh.Vertices.Length);
            // the appended part's indices were offset past the 16-bit ceiling
            Assert.Contains((uint)(ushort.MaxValue + 2), mesh.Indices32);        // 65537
            Assert.All(mesh.Indices32, i => Assert.InRange(i, 0u, (uint)(mesh.Vertices.Length - 1)));
        }

        [Fact]
        public void Skinned_Mesh_Past_65k_Selects_UInt32()
        {
            int n = ushort.MaxValue + 2;
            var verts = new SkinnedVertex[n];
            var idx = new uint[n];
            for (int i = 0; i < n; i++)
            {
                verts[i] = new SkinnedVertex
                {
                    Position = new Vector3(i, 0, 0),
                    Normal = Vector3.UnitY,
                    Color = Vector4.One,
                    BoneIndices = Vector4.Zero,
                    BoneWeights = new Vector4(1, 0, 0, 0),
                };
                idx[i] = (uint)i;
            }
            var ib = new[] { Matrix4x4.Identity };
            var rp = new[] { Matrix4x4.Identity };
            var mesh = new SkinnedGltfMesh(verts, idx, ib, rp);

            Assert.Equal(GpuIndexFormat.UInt32, mesh.IndexFormat);
            Assert.Equal(n, mesh.Vertices.Length);
            Assert.Throws<InvalidOperationException>(() => { _ = mesh.Indices; });
        }

        [Fact]
        public void Skinned_Small_Mesh_Stays_UInt16()
        {
            var verts = new[]
            {
                new SkinnedVertex { Position = Vector3.Zero, Normal = Vector3.UnitY, Color = Vector4.One, BoneWeights = new Vector4(1, 0, 0, 0) },
                new SkinnedVertex { Position = Vector3.UnitX, Normal = Vector3.UnitY, Color = Vector4.One, BoneWeights = new Vector4(1, 0, 0, 0) },
                new SkinnedVertex { Position = Vector3.UnitZ, Normal = Vector3.UnitY, Color = Vector4.One, BoneWeights = new Vector4(1, 0, 0, 0) },
            };
            var mesh = new SkinnedGltfMesh(verts, new ushort[] { 0, 1, 2 },
                new[] { Matrix4x4.Identity }, new[] { Matrix4x4.Identity });
            Assert.Equal(GpuIndexFormat.UInt16, mesh.IndexFormat);
            Assert.Equal(3, mesh.Indices.Length);
        }
    }
}
