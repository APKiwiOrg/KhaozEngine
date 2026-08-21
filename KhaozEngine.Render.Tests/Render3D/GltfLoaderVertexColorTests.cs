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
    /// <summary>Per-vertex COLOR_0 multiplies into the loaded vertex colour on the rigid and the skinned path,
    /// an authored colour seam survives the weld, and an asset without the attribute still loads the flat
    /// material base colour. Every GLB is written by hand here rather than checked in, so the expected colours
    /// are hand-computable from the numbers a few lines above the assertion.</summary>
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

        // Two coplanar triangles sharing the edge (1,0,0)-(0,1,0), the shape a kit piece makes wherever two
        // palette shades meet on a flat face. Six corners, four distinct positions.
        static readonly Vector3[] QuadPositions =
        {
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f),
            new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
        };

        static readonly Vector4 Red = new(1f, 0f, 0f, 1f);
        static readonly Vector4 Blue = new(0f, 0f, 1f, 1f);

        static readonly Vector4[] QuadColors = { Red, Red, Red, Blue, Blue, Blue };

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

        // The regression this weld change exists for: before it, the two shared-edge corners of the blue triangle
        // welded into the red triangle's vertices and came back RED, so the blue face rendered as a gradient.
        [Fact]
        public void A_colour_seam_survives_the_weld()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "seam.glb");
                File.WriteAllBytes(path, BuildGlb(QuadPositions, QuadColors));

                GltfMesh mesh = GltfLoader.Load(path);

                // Four distinct positions, but the two on the seam carry two colours each, so six vertices.
                Assert.Equal(6, mesh.Vertices.Length);
                Assert.Equal(6, mesh.Indices.Length);

                Vector4 first = ColorOfTriangle(mesh, 0);
                Vector4 second = ColorOfTriangle(mesh, 1);
                AssertColorEqual(BaseColorFactor * Red, first);
                AssertColorEqual(BaseColorFactor * Blue, second);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void Color0_multiplies_into_a_skinned_vertex_colour()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "skinned.glb");
                File.WriteAllBytes(path, BuildGlb(Positions, VertexColors, skinned: true));

                SkinnedGltfMesh mesh = GltfLoader.LoadSkinned(path);

                // The skinned path indexes vertices directly (no re-weld, so joints stay aligned), so the source
                // order holds and the same hand-computed products apply.
                Assert.Equal(3, mesh.Vertices.Length);
                AssertColorEqual(new Vector4(0.5f, 0.30f, 0.20f, 1f), mesh.Vertices[0].Color);
                AssertColorEqual(new Vector4(0.25f, 0.60f, 0.40f, 0.5f), mesh.Vertices[1].Color);
                AssertColorEqual(new Vector4(0.125f, 0.15f, 0.80f, 1f), mesh.Vertices[2].Color);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public void A_skinned_asset_without_color0_keeps_the_base_colour_factor()
        {
            string dir = NewTempDir();
            try
            {
                string path = Path.Combine(dir, "skinned-plain.glb");
                File.WriteAllBytes(path, BuildGlb(Positions, null, skinned: true));

                SkinnedGltfMesh mesh = GltfLoader.LoadSkinned(path);
                Assert.Equal(3, mesh.Vertices.Length);
                foreach (SkinnedVertex v in mesh.Vertices) AssertColorEqual(BaseColorFactor, v.Color);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        // The one colour every corner of triangle t resolves to, asserting on the way that the triangle is not
        // split across two shades (which is exactly what the pre-fix weld did to the second triangle).
        static Vector4 ColorOfTriangle(GltfMesh mesh, int t)
        {
            Vector4 color = mesh.Vertices[mesh.Indices[t * 3]].Color;
            for (int i = 1; i < 3; i++)
                AssertColorEqual(color, mesh.Vertices[mesh.Indices[t * 3 + i]].Color);
            return color;
        }

        static void AssertColorEqual(Vector4 expected, Vector4 actual)
        {
            Assert.Equal(expected.X, actual.X, 5);
            Assert.Equal(expected.Y, actual.Y, 5);
            Assert.Equal(expected.Z, actual.Z, 5);
            Assert.Equal(expected.W, actual.W, 5);
        }

        static void AssertColorAt(GltfMesh mesh, Vector3 position, Vector4 expected)
        {
            foreach (ModelVertex v in mesh.Vertices)
            {
                if (Vector3.Distance(v.Position, position) > 1e-5f) continue;
                AssertColorEqual(expected, v.Color);
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

        /// <summary>A minimal GLB: one material with a baseColorFactor, one mesh, one node, one scene, and
        /// POSITION plus an optional float VEC4 COLOR_0. With <paramref name="skinned"/> it also carries
        /// JOINTS_0/WEIGHTS_0 bound to a one-joint skin, which is the least a glb can have and still load through
        /// <see cref="GltfLoader.LoadSkinned"/>.</summary>
        static byte[] BuildGlb(Vector3[] positions, Vector4[]? colors, bool skinned = false)
        {
            var bin = new MemoryStream();
            var writer = new BinaryWriter(bin);
            foreach (Vector3 p in positions) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }

            int colorOffset = (int)bin.Length;
            if (colors is not null)
                foreach (Vector4 c in colors) { writer.Write(c.X); writer.Write(c.Y); writer.Write(c.Z); writer.Write(c.W); }

            int jointsOffset = (int)bin.Length;
            if (skinned)
            {
                // Every vertex fully weighted to joint 0. A zero weight must pair with joint index 0 per spec,
                // which is what the trailing three lanes are.
                for (int i = 0; i < positions.Length; i++) writer.Write(new byte[] { 0, 0, 0, 0 });
                for (int i = 0; i < positions.Length; i++) { writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f); }
                foreach (float f in MatrixFloats(Matrix4x4.Identity)) writer.Write(f);
            }
            writer.Flush();
            byte[] binBytes = bin.ToArray();

            var attributes = new Dictionary<string, object> { ["POSITION"] = 0 };
            var bufferViews = new List<object> { View(0, colorOffset) };
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
                attributes["COLOR_0"] = accessors.Count;
                bufferViews.Add(View(colorOffset, jointsOffset - colorOffset));
                accessors.Add(Accessor(bufferViews.Count - 1, 5126, colors.Length, "VEC4"));
            }

            var root = new Dictionary<string, object>
            {
                ["asset"] = new Dictionary<string, object> { ["version"] = "2.0" },
                ["scene"] = 0,
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
                ["buffers"] = new object[] { new Dictionary<string, object> { ["byteLength"] = binBytes.Length } },
            };

            if (skinned)
            {
                int weightsOffset = jointsOffset + positions.Length * 4;
                int inverseBindOffset = weightsOffset + positions.Length * 16;

                attributes["JOINTS_0"] = accessors.Count;
                bufferViews.Add(View(jointsOffset, positions.Length * 4));
                accessors.Add(Accessor(bufferViews.Count - 1, 5121, positions.Length, "VEC4"));

                attributes["WEIGHTS_0"] = accessors.Count;
                bufferViews.Add(View(weightsOffset, positions.Length * 16));
                accessors.Add(Accessor(bufferViews.Count - 1, 5126, positions.Length, "VEC4"));

                int inverseBind = accessors.Count;
                bufferViews.Add(View(inverseBindOffset, 64));
                accessors.Add(Accessor(bufferViews.Count - 1, 5126, 1, "MAT4"));

                root["skins"] = new object[]
                {
                    new Dictionary<string, object> { ["joints"] = new[] { 1 }, ["inverseBindMatrices"] = inverseBind },
                };
                root["nodes"] = new object[]
                {
                    new Dictionary<string, object> { ["mesh"] = 0, ["skin"] = 0 },
                    new Dictionary<string, object> { ["name"] = "joint" },
                };
                root["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = new[] { 0, 1 } } };
            }
            else
            {
                root["nodes"] = new object[] { new Dictionary<string, object> { ["mesh"] = 0 } };
                root["scenes"] = new object[] { new Dictionary<string, object> { ["nodes"] = new[] { 0 } } };
            }

            root["meshes"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["primitives"] = new object[]
                    {
                        new Dictionary<string, object> { ["attributes"] = attributes, ["material"] = 0, ["mode"] = 4 },
                    },
                },
            };
            root["accessors"] = accessors;
            root["bufferViews"] = bufferViews;

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

        static Dictionary<string, object> View(int offset, int length) =>
            new() { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length };

        static Dictionary<string, object> Accessor(int bufferView, int componentType, int count, string type) =>
            new()
            {
                ["bufferView"] = bufferView, ["componentType"] = componentType, ["count"] = count, ["type"] = type,
            };

        static IEnumerable<float> MatrixFloats(Matrix4x4 m)
        {
            yield return m.M11; yield return m.M12; yield return m.M13; yield return m.M14;
            yield return m.M21; yield return m.M22; yield return m.M23; yield return m.M24;
            yield return m.M31; yield return m.M32; yield return m.M33; yield return m.M34;
            yield return m.M41; yield return m.M42; yield return m.M43; yield return m.M44;
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
