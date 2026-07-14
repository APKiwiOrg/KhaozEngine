using System;
using System.IO;
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
    /// <summary>Headless coverage of the glTF alphaMode/alphaCutoff parse (GltfLoader -> GltfMaterialMaps.AlphaCutoff).
    /// A MASK material must carry its cutoff through both the single-material (LoadWithMaterial) and the
    /// multi-material prop path (LoadPartsWithMaterials) the textured foliage kits use, OPAQUE (and absent alphaMode)
    /// must resolve to 0 (no clip, byte-identical render), and glTF BLEND is treated as MASK per this engine's
    /// documented scope. No GPU: the parse + carry-through is pure.</summary>
    public class AlphaCutoutParseTests
    {
        // A rigid triangle whose one material is configured by <paramref name="configure"/> (alpha mode + a
        // baseColor factor, no texture needed to read the cutoff). Returns the glb path (caller deletes).
        static string WriteTriangleGlb(Action<MaterialBuilder> configure)
        {
            var mat = new MaterialBuilder("m").WithBaseColor(new Vector4(0.4f, 0.7f, 0.2f, 1f));
            configure(mat);
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty>("m");
            var prim = mesh.UsePrimitive(mat);
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(Vector3 p) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ));
            prim.AddTriangle(V(new(0, 0, 0)), V(new(1, 0, 0)), V(new(0, 1, 0)));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_alpha_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        [Fact]
        public void Mask_CarriesCutoff_ThroughLoadWithMaterial_AndParts()
        {
            string path = WriteTriangleGlb(m => m.WithAlpha(AlphaMode.MASK, 0.3f));
            try
            {
                var (_, maps) = GltfLoader.LoadWithMaterial(path);
                Assert.Equal(0.3f, maps.AlphaCutoff, 3);

                var parts = GltfLoader.LoadPartsWithMaterials(path);
                Assert.Single(parts);
                Assert.Equal(0.3f, parts[0].Maps.AlphaCutoff, 3);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Mask_DefaultCutoff_Is0_5_PerSpec()
        {
            // MASK with no explicit cutoff => the glTF spec default 0.5.
            string path = WriteTriangleGlb(m => m.WithAlpha(AlphaMode.MASK));
            try
            {
                var (_, maps) = GltfLoader.LoadWithMaterial(path);
                Assert.Equal(0.5f, maps.AlphaCutoff, 3);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Opaque_ResolvesToZero_NoClip()
        {
            string path = WriteTriangleGlb(m => m.WithAlpha(AlphaMode.OPAQUE));
            try
            {
                var (_, maps) = GltfLoader.LoadWithMaterial(path);
                Assert.Equal(0f, maps.AlphaCutoff);
                Assert.Equal(0f, GltfLoader.LoadPartsWithMaterials(path)[0].Maps.AlphaCutoff);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void AbsentAlphaMode_DefaultsToOpaque_Zero()
        {
            // No WithAlpha call at all: glTF default is OPAQUE, so cutoff 0 (byte-identical to the pre-cutout path).
            string path = WriteTriangleGlb(_ => { });
            try
            {
                var (_, maps) = GltfLoader.LoadWithMaterial(path);
                Assert.Equal(0f, maps.AlphaCutoff);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Blend_TreatedAsMask_ClipsWithNonZeroCutoff()
        {
            // BLEND is out of scope for the mesh pass and documented as treated like MASK, so a translucent-authored
            // leaf still reads as a clipped silhouette rather than a solid quad. glTF only serializes alphaCutoff for
            // MASK, so a BLEND material reports the spec default (0.5); the load-bearing contract is that BLEND is
            // NOT OPAQUE (cutoff > 0, clipping on), not the exact threshold.
            string path = WriteTriangleGlb(m => m.WithAlpha(AlphaMode.BLEND));
            try
            {
                var (_, maps) = GltfLoader.LoadWithMaterial(path);
                Assert.True(maps.AlphaCutoff > 0f, $"BLEND should clip (cutoff > 0), got {maps.AlphaCutoff}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void SurfaceMaps_CarriesCutoff_FromGltfMaterialMaps()
        {
            // Pure plumb check (no GPU): the cutoff rides GltfMaterialMaps and, via Scene3D.SurfaceMaps, the loaded
            // mesh's material state. LoadSurfaceMaps needs a device, so assert the value-type carriers directly.
            var maps = new GltfMaterialMaps(null, null, null, 0.42f);
            Assert.Equal(0.42f, maps.AlphaCutoff, 3);
            var surface = new Scene3D.SurfaceMaps(default, default, default, maps.AlphaCutoff);
            Assert.Equal(0.42f, surface.AlphaCutoff, 3);
        }
    }
}
