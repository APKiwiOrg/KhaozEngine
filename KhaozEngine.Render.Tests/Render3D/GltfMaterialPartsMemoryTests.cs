using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class GltfMaterialPartsMemoryTests
    {
        static byte[] OnePixelPng(byte r, byte g, byte b, byte a = 255)
        {
            using var ms = new MemoryStream();
            void WriteBE(uint v) => ms.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }, 0, 4);
            void Chunk(string type, byte[] data)
            {
                WriteBE((uint)data.Length);
                byte[] t = System.Text.Encoding.ASCII.GetBytes(type);
                ms.Write(t, 0, 4);
                ms.Write(data, 0, data.Length);
                WriteBE(Crc(t.Concat(data).ToArray()));
            }
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);
            Chunk("IHDR", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0 });
            Chunk("IDAT", Zlib(new byte[] { 0, r, g, b, a }));
            Chunk("IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        static byte[] Zlib(byte[] data)
        {
            using var ms = new MemoryStream();
            ms.WriteByte(0x78); ms.WriteByte(0x01);
            using (var ds = new DeflateStream(ms, CompressionLevel.NoCompression, true)) ds.Write(data, 0, data.Length);
            uint adler = Adler32(data);
            ms.Write(new[] { (byte)(adler >> 24), (byte)(adler >> 16), (byte)(adler >> 8), (byte)adler }, 0, 4);
            return ms.ToArray();
        }

        static uint Adler32(byte[] d)
        {
            uint a = 1, b = 0;
            foreach (byte x in d) { a = (a + x) % 65521; b = (b + a) % 65521; }
            return (b << 16) | a;
        }

        static uint Crc(byte[] d)
        {
            uint c = 0xffffffff;
            foreach (byte x in d)
            {
                c ^= x;
                for (int k = 0; k < 8; k++) c = ((c & 1) != 0) ? (0xedb88320 ^ (c >> 1)) : (c >> 1);
            }
            return c ^ 0xffffffff;
        }

        // Two primitives, two materials: a lower quad (material A, red albedo, y in [0,1]) and an upper quad
        // (material B, blue albedo, y in [1,2]). Returns the glb path (caller deletes).
        static string WriteTwoMaterialStackGlb()
        {
            var matA = new MaterialBuilder("lower")
                .WithBaseColor(new SharpGLTF.Memory.MemoryImage(OnePixelPng(220, 20, 20)))
                .WithNormal(new SharpGLTF.Memory.MemoryImage(OnePixelPng(128, 128, 255)))
                .WithMetallicRoughness(new SharpGLTF.Memory.MemoryImage(OnePixelPng(0, 180, 40)));
            var matB = new MaterialBuilder("upper").WithBaseColor(new SharpGLTF.Memory.MemoryImage(OnePixelPng(20, 40, 230)));

            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("stack");
            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(Vector3 p, Vector2 uv) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv));
            var pa = mesh.UsePrimitive(matA);
            pa.AddTriangle(V(new(0, 0, 0), new(0, 0)), V(new(1, 0, 0), new(1, 0)), V(new(1, 1, 0), new(1, 1)));
            pa.AddTriangle(V(new(0, 0, 0), new(0, 0)), V(new(1, 1, 0), new(1, 1)), V(new(0, 1, 0), new(0, 1)));
            var pb = mesh.UsePrimitive(matB);
            pb.AddTriangle(V(new(0, 1, 0), new(0, 0)), V(new(1, 1, 0), new(1, 0)), V(new(1, 2, 0), new(1, 1)));
            pb.AddTriangle(V(new(0, 1, 0), new(0, 0)), V(new(1, 2, 0), new(1, 1)), V(new(0, 2, 0), new(0, 1)));

            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.CreateTranslation(3, 4, 5));
            string path = Path.Combine(Path.GetTempPath(), $"ke_2mat_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Memory_import_preserves_material_order_geometry_maps_and_baked_node_translation(bool slice)
        {
            string path = WriteTwoMaterialStackGlb();
            try
            {
                IReadOnlyList<GltfMeshPart> byPath = GltfLoader.LoadPartsWithMaterials(path);
                byte[] glb = File.ReadAllBytes(path);
                byte[] original = (byte[])glb.Clone();
                ReadOnlyMemory<byte> bytes = glb;
                if (slice)
                {
                    var padded = new byte[glb.Length + 9];
                    Array.Fill(padded, (byte)0xff);
                    glb.CopyTo(padded, 4);
                    bytes = padded.AsMemory(4, glb.Length);
                }
                File.Delete(path);

                IReadOnlyList<GltfMeshPart> byMemory = GltfLoader.LoadPartsWithMaterials(bytes, "two-material-stack");

                Assert.Equal(original, bytes.ToArray());
                Assert.Equal(2, byMemory.Count);
                for (int i = 0; i < byMemory.Count; i++)
                {
                    Assert.Equal(byPath[i].Mesh.Vertices, byMemory[i].Mesh.Vertices);
                    Assert.Equal(byPath[i].Mesh.Indices32, byMemory[i].Mesh.Indices32);
                    Assert.Equal(byPath[i].Mesh.IndexFormat, byMemory[i].Mesh.IndexFormat);
                    AssertMap(byPath[i].Maps.Albedo, byMemory[i].Maps.Albedo);
                    AssertMap(byPath[i].Maps.Normal, byMemory[i].Maps.Normal);
                    AssertMap(byPath[i].Maps.Roughness, byMemory[i].Maps.Roughness);
                    Assert.Equal(byPath[i].Maps.AlphaCutoff, byMemory[i].Maps.AlphaCutoff);
                }
                Assert.Equal(new byte[] { 220, 20, 20, 255 }, byMemory[0].Maps.Albedo!.Value.Rgba);
                Assert.Equal(new byte[] { 20, 40, 230, 255 }, byMemory[1].Maps.Albedo!.Value.Rgba);
                Assert.Equal(new byte[] { 128, 128, 255, 255 }, byMemory[0].Maps.Normal!.Value.Rgba);
                Assert.Equal(new byte[] { 0, 180, 40, 255 }, byMemory[0].Maps.Roughness!.Value.Rgba);
                Assert.Equal(new Vector3(3, 4, 5), byMemory[0].Mesh.Vertices.MinBy(v => v.Position.Y).Position);
                Assert.Equal(5f, byMemory[0].Mesh.Vertices.Max(v => v.Position.Y));
                Assert.Equal(5f, byMemory[1].Mesh.Vertices.Min(v => v.Position.Y));
                Assert.Equal(6f, byMemory[1].Mesh.Vertices.Max(v => v.Position.Y));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Malformed_indices_keep_asset_index_and_vertex_count_diagnostics()
        {
            byte[] glb = Glb(GltfIndexValidationTests.MalformedRigidGltfJson());

            GltfIndexValidationTests.AssertBadIndex(
                () => GltfLoader.LoadPartsWithMaterials(glb, "bad-material-parts"), "bad-material-parts");
        }

        static void AssertMap(DecodedImage? expected, DecodedImage? actual)
        {
            Assert.Equal(expected.HasValue, actual.HasValue);
            if (expected is not DecodedImage image) return;
            Assert.NotNull(actual);
            Assert.Equal(image.Width, actual.Value.Width);
            Assert.Equal(image.Height, actual.Value.Height);
            Assert.Equal(image.Rgba, actual.Value.Rgba);
        }

        static byte[] Glb(string json)
        {
            byte[] text = Encoding.UTF8.GetBytes(json);
            int padded = (text.Length + 3) & ~3;
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(0x46546C67u);
                writer.Write(2u);
                writer.Write((uint)(12 + 8 + padded));
                writer.Write((uint)padded);
                writer.Write(0x4E4F534Au);
                writer.Write(text);
                for (int i = text.Length; i < padded; i++) writer.Write((byte)' ');
            }
            return stream.ToArray();
        }
    }
}
