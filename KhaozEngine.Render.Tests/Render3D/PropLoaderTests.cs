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

        [Fact]
        public void Normalize_ImplausibleDeclaredHeight_Throws()
        {
            GltfMesh raw = Box(0f, 0f, 0f, 1f, 2f, 1f);
            Assert.Throws<InvalidOperationException>(() => PropLoader.Normalize(raw, heightMeters: 5000f));
        }

        [Fact]
        public void Normalize_ImplausibleScale_Throws()
        {
            // A 5000-unit-tall raw mesh declared as 1.8 m needs a 3.6e-4 scale: below MinScale -> wrong units.
            GltfMesh raw = Box(0f, 0f, 0f, 1f, 5000f, 1f);
            Assert.Throws<InvalidOperationException>(() => PropLoader.Normalize(raw, heightMeters: 1.8f));
        }

        [Fact]
        public void Normalize_DegenerateMesh_Throws()
        {
            GltfMesh flat = Box(0f, 0f, 0f, 1f, 0f, 1f);   // zero height
            Assert.Throws<InvalidOperationException>(() => PropLoader.Normalize(flat, heightMeters: 2f));
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
        public void LoadProp_MissingFile_ThrowsWithContext()
        {
            var entry = new AssetEntry("ghost", Path.Combine(Path.GetTempPath(), "does_not_exist_xyz.glb"), 5f, "test", "CC0");
            var ex = Assert.ThrowsAny<Exception>(() => PropLoader.LoadProp(entry));
            Assert.Contains("ghost", ex.Message);
        }
    }
}
