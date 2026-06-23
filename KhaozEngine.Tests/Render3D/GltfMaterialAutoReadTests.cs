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
    /// <summary>Headless tests for the opt-in glTF material texture auto-read (GltfLoader.LoadWithMaterial /
    /// LoadSkinnedWithMaterial + GltfMaterialMaps). No GPU: assert the decoder returns the expected RGBA
    /// dimensions/pixels for albedo/normal/roughness, that a no-texture material yields an all-absent bundle, and
    /// that the default Load path is byte-unchanged by the new code.</summary>
    public class GltfMaterialAutoReadTests
    {
        // ---- Minimal hand-authored PNG (no external image encoder), 1x1 RGBA, fully opaque. The four-channel
        // pixel lets us round-trip a distinct colour per map and prove which texture landed where. ----
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
            ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, 0, 8);      // PNG signature
            Chunk("IHDR", new byte[] { 0, 0, 0, 1, 0, 0, 0, 1, 8, 6, 0, 0, 0 }); // 1x1, 8-bit, RGBA
            Chunk("IDAT", Zlib(new byte[] { 0, r, g, b, a }));                   // filter 0 + one RGBA pixel
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

        // ---- Fixtures ----

        // A rigid textured triangle: baseColor=red, normal=flat-blue, metallicRoughness=green, each a distinct
        // embedded PNG so we can tell the three maps apart. Returns the glb path (caller deletes).
        static string WriteTexturedTriangleGlb()
        {
            byte[] albedoPng = OnePixelPng(200, 10, 10);    // red
            byte[] normalPng = OnePixelPng(128, 128, 255);  // flat tangent-space normal
            byte[] mrPng = OnePixelPng(0, 180, 40);         // packed metal-rough (roughness in .g = 180)

            // MaterialBuilder takes image content via MemoryImage (embedded into the glb on SaveGLB).
            var mat = new MaterialBuilder("textured")
                .WithBaseColor(new SharpGLTF.Memory.MemoryImage(albedoPng))
                .WithNormal(new SharpGLTF.Memory.MemoryImage(normalPng))
                .WithMetallicRoughness(new SharpGLTF.Memory.MemoryImage(mrPng));

            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("m");
            var prim = mesh.UsePrimitive(mat);
            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(Vector3 p, Vector2 uv) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv));
            prim.AddTriangle(V(new(0, 0, 0), new(0, 0)), V(new(1, 0, 0), new(1, 0)), V(new(0, 1, 0), new(0, 1)));

            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_texmat_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        // A rigid triangle with a baseColor FACTOR but no textures (the common "flat colour" material).
        static string WriteUntexturedTriangleGlb()
        {
            var mat = new MaterialBuilder("flat").WithBaseColor(new Vector4(0.2f, 0.6f, 0.9f, 1f));
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("m");
            var prim = mesh.UsePrimitive(mat);
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(Vector3 p) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ));
            prim.AddTriangle(V(new(0, 0, 0)), V(new(1, 0, 0)), V(new(0, 1, 0)));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_flatmat_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        // A textured, rigged triangle (so LoadSkinnedWithMaterial has a skin + a baseColor texture).
        static string WriteTexturedRiggedGlb()
        {
            byte[] albedoPng = OnePixelPng(10, 220, 30);  // green albedo
            var mat = new MaterialBuilder("skintex").WithBaseColor(new SharpGLTF.Memory.MemoryImage(albedoPng));
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("skin");
            var prim = mesh.UsePrimitive(mat);
            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> V(Vector3 p, Vector2 uv, int bone) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv), new VertexJoints4((bone, 1f)));
            prim.AddTriangle(
                V(new(0, 0, 0), new(0, 0), 0),
                V(new(1, 0, 0), new(1, 0), 1),
                V(new(0, 1, 0), new(0, 1), 1));
            var bone0 = new NodeBuilder("bone0");
            var bone1 = bone0.CreateNode("bone1");
            bone1.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);
            var scene = new SceneBuilder();
            scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone0, bone1);
            string path = Path.Combine(Path.GetTempPath(), $"ke_skintex_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        // ---- Tests ----

        [Fact]
        public void LoadWithMaterial_DecodesAlbedoNormalRoughness_ToExpectedRgba()
        {
            string path = WriteTexturedTriangleGlb();
            try
            {
                var (mesh, maps) = GltfLoader.LoadWithMaterial(path);
                Assert.True(mesh.Vertices.Length >= 3);   // mesh still loads
                Assert.False(maps.IsEmpty);

                Assert.NotNull(maps.Albedo);
                Assert.NotNull(maps.Normal);
                Assert.NotNull(maps.Roughness);

                DecodedImage albedo = maps.Albedo!.Value;
                Assert.Equal(1, albedo.Width);
                Assert.Equal(1, albedo.Height);
                Assert.Equal(albedo.Width * albedo.Height * 4, albedo.Rgba.Length);
                // RGBA8, top-left pixel == the red we authored.
                Assert.Equal(200, albedo.Rgba[0]);
                Assert.Equal(10, albedo.Rgba[1]);
                Assert.Equal(10, albedo.Rgba[2]);
                Assert.Equal(255, albedo.Rgba[3]);

                DecodedImage normal = maps.Normal!.Value;
                Assert.Equal(128, normal.Rgba[0]);
                Assert.Equal(128, normal.Rgba[1]);
                Assert.Equal(255, normal.Rgba[2]);   // flat tangent-space normal passed through unchanged

                // metallicRoughness passed through unchanged: roughness lives in .g (= 180), no repack.
                DecodedImage rough = maps.Roughness!.Value;
                Assert.Equal(180, rough.Rgba[1]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadWithMaterial_NoTextures_YieldsAllAbsentMaps()
        {
            string path = WriteUntexturedTriangleGlb();
            try
            {
                var (mesh, maps) = GltfLoader.LoadWithMaterial(path);
                Assert.True(mesh.Vertices.Length >= 3);
                Assert.True(maps.IsEmpty);
                Assert.Null(maps.Albedo);
                Assert.Null(maps.Normal);
                Assert.Null(maps.Roughness);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadWithMaterial_MeshMatches_DefaultLoadPath_Unchanged()
        {
            // The default Load path must be byte-unchanged: the mesh from LoadWithMaterial is identical to Load's.
            string path = WriteTexturedTriangleGlb();
            try
            {
                GltfMesh viaDefault = GltfLoader.Load(path);
                var (viaMaterial, _) = GltfLoader.LoadWithMaterial(path);

                Assert.Equal(viaDefault.Vertices.Length, viaMaterial.Vertices.Length);
                Assert.Equal(viaDefault.Indices32, viaMaterial.Indices32);
                Assert.Equal(viaDefault.IndexFormat, viaMaterial.IndexFormat);
                for (int i = 0; i < viaDefault.Vertices.Length; i++)
                    Assert.Equal(viaDefault.Vertices[i], viaMaterial.Vertices[i]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadSkinnedWithMaterial_ReadsSkinAndAlbedo()
        {
            string path = WriteTexturedRiggedGlb();
            try
            {
                var (mesh, maps) = GltfLoader.LoadSkinnedWithMaterial(path);

                Assert.Equal(2, mesh.BoneCount);                       // skin still loads
                Assert.True(mesh.Vertices.Length >= 3);

                Assert.NotNull(maps.Albedo);                           // baseColor texture decoded
                Assert.Null(maps.Normal);                             // none authored => absent
                Assert.Null(maps.Roughness);
                DecodedImage albedo = maps.Albedo!.Value;
                Assert.Equal(10, albedo.Rgba[0]);
                Assert.Equal(220, albedo.Rgba[1]);
                Assert.Equal(30, albedo.Rgba[2]);

                // The skinned mesh itself matches the default LoadSkinned path (auto-read is additive).
                SkinnedGltfMesh viaDefault = GltfLoader.LoadSkinned(path);
                Assert.Equal(viaDefault.Vertices.Length, mesh.Vertices.Length);
                Assert.Equal(viaDefault.BoneCount, mesh.BoneCount);
            }
            finally { File.Delete(path); }
        }
    }
}
