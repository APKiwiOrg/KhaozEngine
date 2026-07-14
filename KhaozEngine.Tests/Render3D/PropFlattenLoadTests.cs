using System;
using System.Collections.Generic;
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
    /// <summary>Headless tests for load-time albedo flattening (no GPU): <see cref="GltfLoader.AverageAlbedo"/> and
    /// the flat <see cref="PropLoader.LoadProp"/> folding a material's alpha-weighted average albedo into its
    /// flattened vertex colour when it carries a baseColorTexture, plus the <see cref="AssetEntry.Textured"/>-driven
    /// <see cref="PropLoader.LoadPropAuto"/> branch. A material WITHOUT a texture is byte-identical to before (the
    /// goldens-hold guarantee).</summary>
    public class PropFlattenLoadTests
    {
        // ---- Minimal RGBA PNG encoder (no external encoder), row-major top-left origin. ----
        static byte[] RgbaPng(int w, int h, (byte r, byte g, byte b, byte a)[] texels)
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
            var ihdr = new byte[]
            {
                (byte)(w >> 24), (byte)(w >> 16), (byte)(w >> 8), (byte)w,
                (byte)(h >> 24), (byte)(h >> 16), (byte)(h >> 8), (byte)h,
                8, 6, 0, 0, 0,
            };
            Chunk("IHDR", ihdr);
            var raw = new List<byte>();
            for (int y = 0; y < h; y++)
            {
                raw.Add(0);   // filter byte 0 (None) per scanline
                for (int x = 0; x < w; x++)
                {
                    var t = texels[y * w + x];
                    raw.Add(t.r); raw.Add(t.g); raw.Add(t.b); raw.Add(t.a);
                }
            }
            Chunk("IDAT", Zlib(raw.ToArray()));
            Chunk("IEND", Array.Empty<byte>());
            return ms.ToArray();
        }

        static byte[] OnePixelPng(byte r, byte g, byte b, byte a = 255) =>
            RgbaPng(1, 1, new[] { (r, g, b, a) });

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

        // A one-material textured triangle whose albedo is the given PNG. baseColorFactor stays the default white,
        // so the flattened colour equals the averaged albedo.
        static string WriteTexturedTriangleGlb(byte[] albedoPng)
        {
            var mat = new MaterialBuilder("tex").WithBaseColor(new SharpGLTF.Memory.MemoryImage(albedoPng));
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("m");
            var prim = mesh.UsePrimitive(mat);
            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(Vector3 p, Vector2 uv) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv));
            prim.AddTriangle(V(new(0, 0, 0), new(0, 0)), V(new(1, 0, 0), new(1, 0)), V(new(0, 1, 0), new(0, 1)));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_flatten_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        // An untextured two-material stack (baseColorFACTOR only, no textures): a lower quad (green factor) and an
        // upper quad (orange factor). Used to prove flattening is byte-identical to Load for untextured multi-material.
        static string WriteUntexturedTwoMaterialStackGlb()
        {
            var lower = new MaterialBuilder("lower").WithBaseColor(new Vector4(0.1f, 0.7f, 0.2f, 1f));
            var upper = new MaterialBuilder("upper").WithBaseColor(new Vector4(0.9f, 0.5f, 0.1f, 1f));
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("stack");
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(Vector3 p) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ));
            var pa = mesh.UsePrimitive(lower);
            pa.AddTriangle(V(new(0, 0, 0)), V(new(1, 0, 0)), V(new(1, 1, 0)));
            pa.AddTriangle(V(new(0, 0, 0)), V(new(1, 1, 0)), V(new(0, 1, 0)));
            var pb = mesh.UsePrimitive(upper);
            pb.AddTriangle(V(new(0, 1, 0)), V(new(1, 1, 0)), V(new(1, 2, 0)));
            pb.AddTriangle(V(new(0, 1, 0)), V(new(1, 2, 0)), V(new(0, 2, 0)));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_flat2mat_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        // A two-material textured stack (each material a distinct 1x1 albedo). For LoadPropAuto's textured branch.
        static string WriteTexturedTwoMaterialStackGlb()
        {
            var matA = new MaterialBuilder("lower").WithBaseColor(new SharpGLTF.Memory.MemoryImage(OnePixelPng(220, 20, 20)));
            var matB = new MaterialBuilder("upper").WithBaseColor(new SharpGLTF.Memory.MemoryImage(OnePixelPng(20, 40, 230)));
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1>("stack");
            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty> V(Vector3 p, Vector2 uv) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv));
            var pa = mesh.UsePrimitive(matA);
            pa.AddTriangle(V(new(0, 0, 0), new(0, 0)), V(new(1, 0, 0), new(1, 0)), V(new(1, 1, 0), new(1, 1)));
            var pb = mesh.UsePrimitive(matB);
            pb.AddTriangle(V(new(0, 1, 0), new(0, 0)), V(new(1, 1, 0), new(1, 0)), V(new(1, 2, 0), new(1, 1)));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_tex2mat_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        // ---- AverageAlbedo ----

        [Fact]
        public void AverageAlbedo_AlphaWeighted_IgnoresBelowHalfAlpha()
        {
            // Two texels: an opaque red and a fully-transparent blue. Only the opaque texel counts.
            var img = new DecodedImage(new byte[] { 200, 0, 0, 255, 0, 0, 200, 0 }, 2, 1);
            Vector3 avg = GltfLoader.AverageAlbedo(img);
            Assert.Equal(200f / 255f, avg.X, 4);
            Assert.Equal(0f, avg.Y, 4);
            Assert.Equal(0f, avg.Z, 4);
        }

        [Fact]
        public void AverageAlbedo_AllTransparent_FallsBackToPlainAverage()
        {
            // No texel passes alpha >= 0.5: plain average of ALL texels' RGB.
            var img = new DecodedImage(new byte[] { 200, 0, 0, 0, 0, 0, 200, 0 }, 2, 1);
            Vector3 avg = GltfLoader.AverageAlbedo(img);
            Assert.Equal(100f / 255f, avg.X, 4);   // (200 + 0) / 2
            Assert.Equal(0f, avg.Y, 4);
            Assert.Equal(100f / 255f, avg.Z, 4);   // (0 + 200) / 2
        }

        // ---- Flat LoadProp folds averaged albedo into the vertex colour ----

        [Fact]
        public void FlatLoad_TexturedGlb_AveragesAlbedo()
        {
            // Albedo: an opaque red texel + a transparent blue texel. Alpha-weighted average = pure red. The blue is
            // ignored because its alpha < 0.5. baseColorFactor is white, so the flattened colour == the average.
            byte[] png = RgbaPng(2, 1, new[] { ((byte)200, (byte)0, (byte)0, (byte)255), ((byte)0, (byte)0, (byte)200, (byte)0) });
            string path = WriteTexturedTriangleGlb(png);
            try
            {
                var entry = new AssetEntry("t", path, heightMeters: 2f, source: "", license: "", textured: true);
                GltfMesh mesh = PropLoader.LoadProp(entry);
                Assert.NotEmpty(mesh.Vertices);
                foreach (ModelVertex v in mesh.Vertices)
                {
                    Assert.Equal(200f / 255f, v.Color.X, 3);   // red channel = averaged opaque red
                    Assert.Equal(0f, v.Color.Y, 3);
                    Assert.Equal(0f, v.Color.Z, 3);            // blue NOT pulled in (transparent texel ignored)
                    Assert.Equal(1f, v.Color.W, 3);           // factor alpha preserved
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void FlatLoad_UntexturedGlb_UnchangedFactor()
        {
            // The goldens-hold guarantee: an untextured material's flattened colour is EXACTLY its baseColorFactor,
            // and the flattened flat mesh is byte-identical to the plain Load mesh.
            string path = GltfMaterialAutoReadTests.WriteUntexturedTriangleGlb();
            try
            {
                var entry = new AssetEntry("u", path, heightMeters: 2f, source: "", license: "");
                GltfMesh flat = PropLoader.LoadProp(entry);
                foreach (ModelVertex v in flat.Vertices)
                {
                    Assert.Equal(0.2f, v.Color.X, 4);
                    Assert.Equal(0.6f, v.Color.Y, 4);
                    Assert.Equal(0.9f, v.Color.Z, 4);
                    Assert.Equal(1f, v.Color.W, 4);
                }

                // Flatten loader == plain Load for an untextured asset (same topology + attributes, byte-for-byte).
                GltfMesh viaFlatten = GltfLoader.LoadFlattenedAlbedo(path);
                GltfMesh viaLoad = GltfLoader.Load(path);
                Assert.Equal(viaLoad.Vertices.Length, viaFlatten.Vertices.Length);
                Assert.Equal(viaLoad.Indices32, viaFlatten.Indices32);
                for (int i = 0; i < viaLoad.Vertices.Length; i++)
                    Assert.Equal(viaLoad.Vertices[i], viaFlatten.Vertices[i]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void FlatLoad_UntexturedMultiMaterial_ByteIdenticalToLoad()
        {
            // Multi-material untextured props must ALSO stay byte-identical (the flatten resolver reuses the exact
            // same all-corners weld as Load, so vertex dedup across materials is unchanged).
            string path = WriteUntexturedTwoMaterialStackGlb();
            try
            {
                GltfMesh viaFlatten = GltfLoader.LoadFlattenedAlbedo(path);
                GltfMesh viaLoad = GltfLoader.Load(path);
                Assert.Equal(viaLoad.Vertices.Length, viaFlatten.Vertices.Length);
                Assert.Equal(viaLoad.Indices32, viaFlatten.Indices32);
                for (int i = 0; i < viaLoad.Vertices.Length; i++)
                    Assert.Equal(viaLoad.Vertices[i], viaFlatten.Vertices[i]);
            }
            finally { File.Delete(path); }
        }

        // ---- Textured-flag convenience loader ----

        [Fact]
        public void TexturedFlag_BranchesLoader()
        {
            string path = WriteTexturedTwoMaterialStackGlb();
            try
            {
                // Textured => multi-part, one textured part per source material, maps present.
                var texEntry = new AssetEntry("stack", path, heightMeters: 4f, source: "", license: "", textured: true);
                IReadOnlyList<GltfMeshPart> tex = PropLoader.LoadPropAuto(texEntry);
                Assert.Equal(2, tex.Count);
                Assert.False(tex[0].Maps.IsEmpty);
                Assert.False(tex[1].Maps.IsEmpty);

                // Untextured flag => single flattened part, no maps (renders flat).
                var flatEntry = new AssetEntry("stack", path, heightMeters: 4f, source: "", license: "", textured: false);
                IReadOnlyList<GltfMeshPart> flat = PropLoader.LoadPropAuto(flatEntry);
                Assert.Single(flat);
                Assert.True(flat[0].Maps.IsEmpty);
                Assert.NotEmpty(flat[0].Mesh.Vertices);
            }
            finally { File.Delete(path); }
        }
    }
}
