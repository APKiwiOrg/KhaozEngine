using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>Headless tests for the prop asset pipeline (no GPU): mesh normalization to a declared height with
    /// origin-at-base + X/Z re-centring, the human-scale validation guard, and loading a manifest entry's glTF
    /// (built in-process via SharpGLTF, like the GltfLoader tests).</summary>
    public class PropLoaderTests
    {
        // ---- helpers ----
        static ModelVertex V(float x, float y, float z) =>
            new ModelVertex(new Vector3(x, y, z), Vector3.UnitY, new Vector4(1, 1, 1, 1), Vector2.Zero, Vector4.Zero);

        // A box mesh with the given axis-aligned bounds (Normalize only reads vertex positions for the bbox).
        static GltfMesh Box(float minX, float minY, float minZ, float maxX, float maxY, float maxZ)
        {
            var v = new[]
            {
                V(minX, minY, minZ), V(maxX, minY, minZ), V(maxX, maxY, minZ), V(minX, maxY, minZ),
                V(minX, minY, maxZ), V(maxX, minY, maxZ), V(maxX, maxY, maxZ), V(minX, maxY, maxZ),
            };
            var idx = new ushort[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4 };
            return new GltfMesh(v, idx);
        }

        static (Vector3 min, Vector3 max) Bbox(GltfMesh m)
        {
            var mn = new Vector3(float.MaxValue);
            var mx = new Vector3(float.MinValue);
            foreach (var v in m.Vertices) { mn = Vector3.Min(mn, v.Position); mx = Vector3.Max(mx, v.Position); }
            return (mn, mx);
        }

        // ---- Normalize ----

        [Fact]
        public void Normalize_ScalesToDeclaredHeight_BaseAtZero_CentredXZ()
        {
            // 2 m-tall box, off-centre in X (2..4) and Y lifted (1..3).
            GltfMesh raw = Box(2f, 1f, -1f, 4f, 3f, 1f);
            GltfMesh norm = PropLoader.Normalize(raw, heightMeters: 14f);

            (Vector3 mn, Vector3 mx) = Bbox(norm);
            Assert.Equal(14f, mx.Y - mn.Y, 3);                 // scaled to declared height
            Assert.Equal(0f, mn.Y, 3);                         // base dropped to y=0
            Assert.Equal(0f, (mn.X + mx.X) * 0.5f, 3);         // X re-centred on origin
            Assert.Equal(0f, (mn.Z + mx.Z) * 0.5f, 3);         // Z re-centred on origin
        }

        [Theory]
        [InlineData(2f, 5000f, PropLoadRefusal.DeclaredHeightOutsideRange)]
        [InlineData(2f, .01f, PropLoadRefusal.DeclaredHeightOutsideRange)]
        [InlineData(8192f, 2f, PropLoadRefusal.ScaleOutsideRange)]
        [InlineData(1f / 8192, 2f, PropLoadRefusal.ScaleOutsideRange)]
        [InlineData(0f, 2f, PropLoadRefusal.NoMeasurableHeight)]
        public void Normalize_rejects_implausible_or_degenerate_height(
            float rawHeight, float declaredHeight, PropLoadRefusal reason)
        {
            GltfMesh raw = Box(0f, 0f, 0f, 1f, rawHeight, 1f);
            var parts = new[] { new GltfMeshPart(raw, default) };
            Action[] loads =
            {
                () => PropLoader.Normalize(raw, declaredHeight),
                () => PropLoader.NormalizeParts(parts, declaredHeight),
            };
            foreach (Action load in loads)
            {
                var ex = Assert.Throws<PropLoadStateException>(load);
                Assert.Equal(reason, ex.Reason);
                Assert.Null(ex.EntryId);
                Assert.IsAssignableFrom<InvalidOperationException>(ex);
                if (reason == PropLoadRefusal.DeclaredHeightOutsideRange)
                {
                    Assert.Equal(declaredHeight, ex.HeightMeters);
                    Assert.Equal(PropValidation.Default.MinHeightMeters, ex.MinimumHeightMeters);
                    Assert.Equal(PropValidation.Default.MaxHeightMeters, ex.MaximumHeightMeters);
                }
                else
                {
                    Assert.Equal(rawHeight, ex.RawHeight);
                    if (reason == PropLoadRefusal.ScaleOutsideRange)
                    {
                        Assert.Equal(rawHeight == 8192f ? 1f / 4096 : 16384f, ex.Scale);
                        Assert.Equal(declaredHeight, ex.HeightMeters);
                        Assert.Equal(PropValidation.Default.MinScale, ex.MinimumScale);
                        Assert.Equal(PropValidation.Default.MaxScale, ex.MaximumScale);
                    }
                }
            }
            Assert.NotNull(PropLoader.Normalize(Box(0, 0, 0, 1, 2, 1), 2));
            Assert.Single(PropLoader.NormalizeParts(new[] { new GltfMeshPart(Box(0, 0, 0, 1, 2, 1), default) }, 2));
        }

        [Fact]
        public void NormalizeParts_requires_a_material_part()
        {
            var ex = Assert.Throws<PropLoadArgumentException>(
                () => PropLoader.NormalizeParts(Array.Empty<GltfMeshPart>(), 2));
            Assert.Equal(PropLoadRefusal.MaterialPartsEmpty, ex.Reason);
            Assert.Equal(0, ex.PartCount);
            Assert.Equal("parts", ex.ParamName);
            Assert.IsAssignableFrom<ArgumentException>(ex);
            Assert.Single(PropLoader.NormalizeParts(new[] { new GltfMeshPart(Box(0, 0, 0, 1, 2, 1), default) }, 2));
        }

        [Fact]
        public void Normalize_RejectsMalformedIndicesBeforeTransformingThem()
        {
            GltfMesh raw = Box(0f, 0f, 0f, 1f, 2f, 1f);
            raw = new GltfMesh(raw.Vertices, new uint[] { 0, 1, 9 });

            var ex = Assert.Throws<MeshIndexStateException>(
                () => PropLoader.Normalize(raw, heightMeters: 2f));

            Assert.Equal(MeshIndexRefusal.SourceIndexOutsideVertexCount, ex.Reason);
            Assert.Equal(9L, ex.Index);
            Assert.Equal(8, ex.VertexCount);
            Assert.Equal("prop", ex.Identity);
            Assert.Null(ex.EntryId);
            Assert.NotNull(PropLoader.Normalize(new GltfMesh(raw.Vertices, new uint[] { 0, 1, 2 }), 2));
        }

        [Fact]
        public void NormalizeParts_RejectsMalformedPartWithItsPartNumber()
        {
            GltfMesh good = Box(0f, 0f, 0f, 1f, 2f, 1f);
            GltfMesh bad = new(good.Vertices, new uint[] { 0, 1, uint.MaxValue });
            var parts = new[]
            {
                new GltfMeshPart(good, default),
                new GltfMeshPart(bad, default),
            };

            var ex = Assert.Throws<MeshIndexStateException>(
                () => PropLoader.NormalizeParts(parts, heightMeters: 2f));

            Assert.Equal(MeshIndexRefusal.SourceIndexOutsideVertexCount, ex.Reason);
            Assert.Equal(1, ex.PartIndex);
            Assert.Equal((long)uint.MaxValue, ex.Index);
            Assert.Equal(8, ex.VertexCount);
            Assert.Equal("prop", ex.Identity);
            Assert.Null(ex.EntryId);
            parts[1] = new GltfMeshPart(good, default);
            Assert.Equal(2, PropLoader.NormalizeParts(parts, 2).Count);
        }

        // ---- LoadProp (in-process glb) ----

        // A 2 m-tall box glb (Y from 0..2, X 1..3) so we can prove LoadProp normalizes a real loaded mesh.
        static string WriteBoxGlb()
        {
            var mat = new MaterialBuilder("flat").WithBaseColor(new Vector4(0.3f, 0.5f, 0.2f, 1f));
            var mesh = new MeshBuilder<VertexPositionNormal>("box");
            var prim = mesh.UsePrimitive(mat);
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> P(float x, float y, float z) =>
                new(new VertexPositionNormal(new Vector3(x, y, z), Vector3.UnitY));
            // two triangles spanning x[1,3], y[0,2], z=0 (height 2)
            prim.AddTriangle(P(1, 0, 0), P(3, 0, 0), P(3, 2, 0));
            prim.AddTriangle(P(1, 0, 0), P(3, 2, 0), P(1, 2, 0));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_box_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        [Fact]
        public void LoadProp_NormalizesInProcessGlb()
        {
            string path = WriteBoxGlb();
            try
            {
                var entry = new AssetEntry("box", path, heightMeters: 11f, "test", "CC0");
                GltfMesh mesh = PropLoader.LoadProp(entry);
                (Vector3 mn, Vector3 mx) = Bbox(mesh);
                Assert.Equal(11f, mx.Y - mn.Y, 2);
                Assert.Equal(0f, mn.Y, 2);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadProp_MissingFile_PreservesTheLoadFailure()
        {
            var entry = new AssetEntry("ghost", Path.Combine(Path.GetTempPath(), "does_not_exist_xyz.glb"), 5f, "test", "CC0");
            var ex = Assert.Throws<PropLoadStateException>(() => PropLoader.LoadProp(entry));
            Assert.Equal(PropLoadRefusal.AssetLoadFailed, ex.Reason);
            Assert.Equal(entry.File, ex.Path);
            Assert.NotNull(ex.InnerException);
            // LoadProp promises the entry identity in its failure.
            Assert.Equal("ghost", ex.EntryId);
        }

        // ---- LoadPropLodAuto (author-supplied far LOD variant) ----

        [Fact]
        public void LoadPropLodAuto_NoLodFile_ReturnsNull()
        {
            // No lodFile declared -> no variant, the caller keeps the full mesh at every distance (unchanged behaviour).
            var entry = new AssetEntry("box", "box.glb", heightMeters: 11f, "test", "CC0");
            Assert.Null(PropLoader.LoadPropLodAuto(entry));
        }

        [Fact]
        public void LoadPropLodAuto_LoadsVariant_NormalizedToDeclaredHeight()
        {
            // The LOD glb is a separate low-poly file; it normalizes to the SAME declared height as the full mesh so
            // the runtime swap is size-stable. Untextured entry -> exactly one flat part.
            string lodPath = WriteBoxGlb();
            try
            {
                var entry = new AssetEntry("box", "box.glb", heightMeters: 11f, "test", "CC0", lodFile: lodPath);
                var parts = PropLoader.LoadPropLodAuto(entry);
                Assert.NotNull(parts);
                Assert.Single(parts!);
                (Vector3 mn, Vector3 mx) = Bbox(parts![0].Mesh);
                Assert.Equal(11f, mx.Y - mn.Y, 2);
                Assert.Equal(0f, mn.Y, 2);
            }
            finally { File.Delete(lodPath); }
        }

        [Fact]
        public void LoadPropLodAuto_MissingLodFile_PreservesTheLoadFailure()
        {
            var entry = new AssetEntry("stump", "stump.glb", 5f, "test", "CC0",
                lodFile: Path.Combine(Path.GetTempPath(), "no_such_lod_xyz.glb"));
            var ex = Assert.Throws<PropLoadStateException>(() => PropLoader.LoadPropLodAuto(entry));
            Assert.Equal(PropLoadRefusal.AssetLoadFailed, ex.Reason);
            Assert.Equal(entry.LodFile, ex.Path);
            Assert.NotNull(ex.InnerException);
            // LoadPropLodAuto promises the entry identity in its failure.
            Assert.Equal("stump", ex.EntryId);
        }
    }
}
