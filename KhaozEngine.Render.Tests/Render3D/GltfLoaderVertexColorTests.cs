using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Per-vertex COLOR_0 multiplies into the loaded vertex colour, and an asset without the attribute
    /// still loads the flat material base colour. Both GLBs are written by hand here rather than checked in, so
    /// the expected colours are hand-computable from the numbers a few lines above the assertion.</summary>
    public class GltfLoaderVertexColorTests
    {
        static readonly Vector4 BaseColorFactor = new(0.5f, 0.6f, 0.8f, 1f);

        static readonly Vector3[] Positions =
        {
            new(0f, 0f, 0f),
            new(1f, 0f, 0f),
            new(0f, 1f, 0f),
        };

        static readonly Vector4[] VertexColors =
        {
            new(1f, 0.5f, 0.25f, 1f),
            new(0.5f, 1f, 0.5f, 0.5f),
            new(0.25f, 0.25f, 1f, 1f),
        };

        [Fact]
        public void Color0_multiplies_into_the_vertex_colour()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "colored.glb");
                File.WriteAllBytes(path, BuildGlb(Positions, VertexColors));

                GltfMesh mesh = GltfLoader.Load(path);
                Assert.Equal(3, mesh.Vertices.Length);

                // Hand-computed baseColorFactor * COLOR_0, per vertex, matched by position because the weld does
                // not promise input order.
                AssertColorAt(mesh, Positions[0], new Vector4(0.5f, 0.30f, 0.20f, 1f));
                AssertColorAt(mesh, Positions[1], new Vector4(0.25f, 0.60f, 0.40f, 0.5f));
                AssertColorAt(mesh, Positions[2], new Vector4(0.125f, 0.15f, 0.80f, 1f));
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Without_color0_every_vertex_keeps_the_base_colour_factor()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "plain.glb");
                File.WriteAllBytes(path, BuildGlb(Positions, null));

                GltfMesh mesh = GltfLoader.Load(path);
                Assert.Equal(3, mesh.Vertices.Length);
                foreach (Vector3 p in Positions) AssertColorAt(mesh, p, BaseColorFactor);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        static void AssertColorAt(GltfMesh mesh, Vector3 position, Vector4 expected)
        {
            foreach (ModelVertex v in mesh.Vertices)
            {
                if (Vector3.Distance(v.Position, position) > 1e-5f) continue;
                Assert.Equal(expected.X, v.Color.X, 5);
                Assert.Equal(expected.Y, v.Color.Y, 5);
                Assert.Equal(expected.Z, v.Color.Z, 5);
                Assert.Equal(expected.W, v.Color.W, 5);
                return;
            }
            Assert.Fail($"no loaded vertex at {position}");
        }

        static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ke-gltf-color-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A minimal single-triangle GLB: one material with a baseColorFactor, one mesh, one node, one
        /// scene, and POSITION plus an optional float VEC4 COLOR_0.</summary>
        static byte[] BuildGlb(Vector3[] positions, Vector4[]? colors)
        {
            var bin = new MemoryStream();
            var writer = new BinaryWriter(bin);
            foreach (Vector3 p in positions) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
            int colorOffset = (int)bin.Length;
            if (colors is not null)
                foreach (Vector4 c in colors) { writer.Write(c.X); writer.Write(c.Y); writer.Write(c.Z); writer.Write(c.W); }
            writer.Flush();
            byte[] binBytes = bin.ToArray();

            var attributes = new Dictionary<string, object> { ["POSITION"] = 0 };
            var bufferViews = new List<object>
            {
                new Dictionary<string, object> { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = colorOffset },
            };
            var accessors = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["bufferView"] = 0, ["componentType"] = 5126, ["count"] = positions.Length, ["type"] = "VEC3",
                    ["min"] = Bound(positions, min: true), ["max"] = Bound(positions, min: false),
                },
            };
            if (colors is not null)
            {
                attributes["COLOR_0"] = 1;
                bufferViews.Add(new Dictionary<string, object>
                {
                    ["buffer"] = 0, ["byteOffset"] = colorOffset, ["byteLength"] = binBytes.Length - colorOffset,
                });
                accessors.Add(new Dictionary<string, object>
                {
                    ["bufferView"] = 1, ["componentType"] = 5126, ["count"] = colors.Length, ["type"] = "VEC4",
                });
            }

            var root = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0" },
                ["scene"] = 0,
                ["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = new[] { 0 } } },
                ["nodes"] = new object[] { new Dictionary<string, object> { ["mesh"] = 0 } },
                ["meshes"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["primitives"] = new object[]
                        {
                            new Dictionary<string, object> { ["attributes"] = attributes, ["material"] = 0, ["mode"] = 4 },
                        },
                    },
                },
                ["materials"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["pbrMetallicRoughness"] = new Dictionary<string, object>
                        {
                            ["baseColorFactor"] = new[] { BaseColorFactor.X, BaseColorFactor.Y, BaseColorFactor.Z, BaseColorFactor.W },
                            ["metallicFactor"] = 0f,
                            ["roughnessFactor"] = 1f,
                        },
                    },
                },
                ["accessors"] = accessors,
                ["bufferViews"] = bufferViews,
                ["buffers"] = new object[] { new Dictionary<string, object> { ["byteLength"] = binBytes.Length } },
            };

            byte[] json = Pad(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(root)), 0x20);
            byte[] binChunk = Pad(binBytes, 0x00);

            var glb = new MemoryStream();
            var glbWriter = new BinaryWriter(glb);
            glbWriter.Write(0x46546C67u);                       // "glTF"
            glbWriter.Write(2u);                                // version
            glbWriter.Write((uint)(12 + 8 + json.Length + 8 + binChunk.Length));
            glbWriter.Write((uint)json.Length);
            glbWriter.Write(0x4E4F534Au);                       // "JSON"
            glbWriter.Write(json);
            glbWriter.Write((uint)binChunk.Length);
            glbWriter.Write(0x004E4942u);                       // "BIN\0"
            glbWriter.Write(binChunk);
            glbWriter.Flush();
            return glb.ToArray();
        }

        static float[] Bound(Vector3[] positions, bool min)
        {
            Vector3 acc = positions[0];
            foreach (Vector3 p in positions) acc = min ? Vector3.Min(acc, p) : Vector3.Max(acc, p);
            return new[] { acc.X, acc.Y, acc.Z };
        }

        /// <summary>A GLB chunk is padded to a four-byte boundary, JSON with spaces and BIN with zeros.</summary>
        static byte[] Pad(byte[] data, byte filler)
        {
            int padded = (data.Length + 3) & ~3;
            if (padded == data.Length) return data;
            var result = new byte[padded];
            Array.Copy(data, result, data.Length);
            for (int i = data.Length; i < padded; i++) result[i] = filler;
            return result;
        }
    }
}
