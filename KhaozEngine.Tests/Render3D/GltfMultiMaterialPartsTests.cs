using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless tests for the multi-texture-per-primitive path: GltfLoader.LoadPartsWithMaterials splits a
    /// multi-material glTF into one welded GltfMeshPart per source material (each with its own decoded maps), a
    /// single-material asset degrades to one part byte-identical to Load, and PropLoader.LoadPropParts normalizes
    /// every part by ONE shared transform so the parts stay aligned. No GPU.</summary>
    public class GltfMultiMaterialPartsTests
    {
        // ---- Minimal 1x1 RGBA PNG (no external encoder), so each material carries a distinct colour we can tell
        // apart after decode. Same technique as GltfMaterialAutoReadTests. ----
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
            var matA = new MaterialBuilder("lower").WithBaseColor(new SharpGLTF.Memory.MemoryImage(OnePixelPng(220, 20, 20)));
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
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_2mat_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        [Fact]
        public void LoadPartsWithMaterials_TwoMaterials_YieldsTwoPartsEachWithItsOwnAlbedo()
        {
            string path = WriteTwoMaterialStackGlb();
            try
            {
                var parts = GltfLoader.LoadPartsWithMaterials(path);
                Assert.Equal(2, parts.Count);

                // Each part carries its own decoded albedo; the two are the distinct colours we authored.
                foreach (GltfMeshPart part in parts)
                {
                    Assert.False(part.Maps.IsEmpty);
                    Assert.NotNull(part.Maps.Albedo);
                    Assert.NotEmpty(part.Mesh.Vertices);
                }
                DecodedImage a0 = parts[0].Maps.Albedo!.Value;
                DecodedImage a1 = parts[1].Maps.Albedo!.Value;
                // Part 0 = lower/red, part 1 = upper/blue (stable first-use material order).
                Assert.Equal((220, 20, 20), (a0.Rgba[0], a0.Rgba[1], a0.Rgba[2]));
                Assert.Equal((20, 40, 230), (a1.Rgba[0], a1.Rgba[1], a1.Rgba[2]));
                // Distinct textures, not one stretched over both: the two albedos differ.
                Assert.NotEqual(a0.Rgba[2], a1.Rgba[2]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadPartsWithMaterials_GeometrySplitsByMaterial_LowerVsUpper()
        {
            string path = WriteTwoMaterialStackGlb();
            try
            {
                var parts = GltfLoader.LoadPartsWithMaterials(path);
                float Top(GltfMeshPart p) => p.Mesh.Vertices.Max(v => v.Position.Y);
                float Bottom(GltfMeshPart p) => p.Mesh.Vertices.Min(v => v.Position.Y);
                // Lower part spans y in [0,1], upper part y in [1,2]: the split is by material, not co-mingled.
                Assert.Equal(0f, Bottom(parts[0]), 3);
                Assert.Equal(1f, Top(parts[0]), 3);
                Assert.Equal(1f, Bottom(parts[1]), 3);
                Assert.Equal(2f, Top(parts[1]), 3);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadPartsWithMaterials_SingleMaterial_OnePart_MeshByteIdenticalToLoad()
        {
            // A one-material textured triangle: exactly one part, geometry byte-identical to the flattened Load.
            string path = GltfMaterialAutoReadTests.WriteTexturedTriangleGlb();
            try
            {
                var parts = GltfLoader.LoadPartsWithMaterials(path);
                Assert.Single(parts);

                GltfMesh viaLoad = GltfLoader.Load(path);
                GltfMesh viaPart = parts[0].Mesh;
                Assert.Equal(viaLoad.Vertices.Length, viaPart.Vertices.Length);
                Assert.Equal(viaLoad.Indices32, viaPart.Indices32);
                Assert.Equal(viaLoad.IndexFormat, viaPart.IndexFormat);
                for (int i = 0; i < viaLoad.Vertices.Length; i++)
                    Assert.Equal(viaLoad.Vertices[i], viaPart.Vertices[i]);

                // And the one part's maps equal LoadWithMaterial's (albedo authored red).
                Assert.NotNull(parts[0].Maps.Albedo);
                Assert.Equal(200, parts[0].Maps.Albedo!.Value.Rgba[0]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadPropParts_NormalizesWholePropByOneTransform_PartsStayAligned()
        {
            string path = WriteTwoMaterialStackGlb();
            try
            {
                // Raw prop spans y in [0,2]; declare 4 m so the shared scale is 2x.
                var entry = new AssetEntry("stack", path, heightMeters: 4f, source: "", license: "", textured: true);
                var parts = PropLoader.LoadPropParts(entry);
                Assert.Equal(2, parts.Count);

                float lowerBottom = parts[0].Mesh.Vertices.Min(v => v.Position.Y);
                float lowerTop = parts[0].Mesh.Vertices.Max(v => v.Position.Y);
                float upperBottom = parts[1].Mesh.Vertices.Min(v => v.Position.Y);
                float upperTop = parts[1].Mesh.Vertices.Max(v => v.Position.Y);

                // Base dropped to 0, whole prop scaled to 4 m, and the two parts still meet at the seam (2 m):
                // one shared transform, never per-part normalization.
                Assert.Equal(0f, lowerBottom, 3);
                Assert.Equal(2f, lowerTop, 3);
                Assert.Equal(2f, upperBottom, 3);
                Assert.Equal(4f, upperTop, 3);

                // Maps survive normalization unchanged.
                Assert.Equal(220, parts[0].Maps.Albedo!.Value.Rgba[0]);
                Assert.Equal(230, parts[1].Maps.Albedo!.Value.Rgba[2]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadPropParts_ImplausibleHeight_Throws()
        {
            string path = WriteTwoMaterialStackGlb();
            try
            {
                var entry = new AssetEntry("stack", path, heightMeters: 500f, source: "", license: "", textured: true);
                Assert.Throws<InvalidOperationException>(() => PropLoader.LoadPropParts(entry));
            }
            finally { File.Delete(path); }
        }
    }
}
