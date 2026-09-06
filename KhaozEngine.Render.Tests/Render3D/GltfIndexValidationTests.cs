using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public sealed class GltfIndexValidationTests
    {
        static string WriteMalformedRigidGltf()
        {
            var data = new byte[42];
            float[] positions =
            {
                0f, 0f, 0f,
                1f, 0f, 0f,
                0f, 2f, 0f,
            };
            Buffer.BlockCopy(positions, 0, data, 0, 36);
            ushort[] indices = { 0, 1, 7 };
            Buffer.BlockCopy(indices, 0, data, 36, 6);

            object document = new
            {
                asset = new { version = "2.0" },
                buffers = new[] { new { byteLength = data.Length, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(data) } },
                bufferViews = new[]
                {
                    new { buffer = 0, byteOffset = 0, byteLength = 36, target = 34962 },
                    new { buffer = 0, byteOffset = 36, byteLength = 6, target = 34963 },
                },
                accessors = new object[]
                {
                    new { bufferView = 0, componentType = 5126, count = 3, type = "VEC3", min = new[] { 0f, 0f, 0f }, max = new[] { 1f, 2f, 0f } },
                    new { bufferView = 1, componentType = 5123, count = 3, type = "SCALAR" },
                },
                meshes = new[] { new { primitives = new[] { new { attributes = new Dictionary<string, int> { ["POSITION"] = 0 }, indices = 1, mode = 4 } } } },
                nodes = new[] { new { mesh = 0 } },
                scenes = new[] { new { nodes = new[] { 0 } } },
                scene = 0,
            };
            string path = Path.Combine(Path.GetTempPath(), $"ke_bad_index_{Guid.NewGuid():N}.gltf");
            File.WriteAllText(path, JsonSerializer.Serialize(document));
            return path;
        }

        static void AssertBadIndex(Action load, string identity)
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(load);
            Assert.Contains(identity, ex.Message);
            Assert.Contains("index 7", ex.Message);
            Assert.Contains("vertex count 3", ex.Message);
        }

        [Fact]
        public void EveryRigidGltfLoadShapeRejectsMalformedIndicesWithFileIdentity()
        {
            string path = WriteMalformedRigidGltf();
            try
            {
                AssertBadIndex(() => GltfLoader.Load(path), path);
                AssertBadIndex(() => GltfLoader.LoadGroups(path), path);
                AssertBadIndex(() => GltfLoader.LoadWithMaterial(path), path);
                AssertBadIndex(() => GltfLoader.LoadFlattenedAlbedo(path), path);
                AssertBadIndex(() => GltfLoader.LoadPartsWithMaterials(path), path);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SkinnedAndAnimationLoadShapesRejectMalformedIndicesWithFileIdentity()
        {
            string path = WriteMalformedRigidGltf();
            try
            {
                AssertBadIndex(() => GltfLoader.LoadSkinned(path), path);
                AssertBadIndex(() => GltfLoader.LoadSkinnedWithMaterial(path), path);
                AssertBadIndex(() => GltfLoader.LoadAnimations(path), path);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void ManifestAndLodLoadsRejectMalformedIndicesWithPropIdentity()
        {
            string path = WriteMalformedRigidGltf();
            try
            {
                var full = new AssetEntry("bad-oak", path, 2f, "test", "CC0");
                AssertBadIndex(() => PropLoader.LoadProp(full), "bad-oak");
                AssertBadIndex(() => PropLoader.LoadPropWithMaterial(full), "bad-oak");
                AssertBadIndex(() => PropLoader.LoadPropParts(full), "bad-oak");
                AssertBadIndex(() => PropLoader.LoadPropAuto(full), "bad-oak");

                var textured = new AssetEntry("bad-oak-textured", path, 2f, "test", "CC0", textured: true);
                AssertBadIndex(() => PropLoader.LoadPropAuto(textured), "bad-oak-textured");

                var lod = new AssetEntry("bad-oak-lod", "unused.glb", 2f, "test", "CC0", lodFile: path);
                AssertBadIndex(() => PropLoader.LoadPropLodAuto(lod), "bad-oak-lod");
                var texturedLod = new AssetEntry("bad-oak-textured-lod", "unused.glb", 2f, "test", "CC0",
                    textured: true, lodFile: path);
                AssertBadIndex(() => PropLoader.LoadPropLodAuto(texturedLod), "bad-oak-textured-lod");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NegativeSourceIndexIsRejectedBeforeRebase()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => MeshIndexValidation.Rebase(-1, vertexCount: 3, baseIndex: 10, "rig.glb"));

            Assert.Contains("index -1", ex.Message);
            Assert.Contains("vertex count 3", ex.Message);
            Assert.Contains("rig.glb", ex.Message);
        }

        [Fact]
        public void RebaseOverflowIsRejectedWithAssetIdentity()
        {
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => MeshIndexValidation.Rebase(1, vertexCount: 3, baseIndex: uint.MaxValue, "huge-rig.glb"));

            Assert.Contains("overflows", ex.Message);
            Assert.Contains("huge-rig.glb", ex.Message);
        }
    }
}
