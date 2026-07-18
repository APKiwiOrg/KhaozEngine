using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;

namespace KhaozEngine.Tests.MapEditor
{
    // Wave-3 split duplication (same pattern as FakeAppDataEnvironment): the canonical copy of these rigid
    // triangle-glb writers lives in KhaozEngine.Render.Tests/Render3D/GltfMaterialAutoReadTests.cs (consumed by
    // the Render3D material-load tests). ViewportWorldTests here also needs them, and they are pure SharpGLTF
    // fixtures with no KhaozEngine dependency, so a small self-contained copy avoids MapEditor's tests taking a
    // reference on the whole Render.Tests graph (which would poison affected-set selection). Moves with
    // ViewportWorldTests into KhaozEngine.MapEditor.Tests in the next wave.

    /// <summary>Headless SharpGLTF fixtures: writes a rigid textured / untextured triangle .glb to a temp file
    /// (caller deletes). Distinct embedded PNGs per map so the loaded maps can be told apart.</summary>
    internal static class GltfTriangleFixtures
    {
        // ---- Minimal hand-authored PNG (no external image encoder), 1x1 RGBA, fully opaque. ----
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

        // A rigid textured triangle: baseColor=red, normal=flat-blue, metallicRoughness=green, each a distinct
        // embedded PNG. Returns the glb path (caller deletes).
        internal static string WriteTexturedTriangleGlb()
        {
            byte[] albedoPng = OnePixelPng(200, 10, 10);    // red
            byte[] normalPng = OnePixelPng(128, 128, 255);  // flat tangent-space normal
            byte[] mrPng = OnePixelPng(0, 180, 40);         // packed metal-rough (roughness in .g = 180)

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
        internal static string WriteUntexturedTriangleGlb()
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
    }
}
