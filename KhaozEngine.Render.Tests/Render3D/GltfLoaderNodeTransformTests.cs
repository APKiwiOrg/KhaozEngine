using System;
using System.Collections.Generic;
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
    // Rigid glTF positions geometry via the scene graph (Blender exports, multi-piece / instanced kits).
    // BuildRigid must bake each mesh node's world matrix into the loaded vertices - POSITION by the world
    // matrix, NORMAL + TANGENT.xyz by the normal matrix (inverse-transpose) - matching the skinned path,
    // while leaving identity-node assets byte-identical to the old raw-accessor output.
    public class GltfLoaderNodeTransformTests
    {
        // One triangle (3 positions, one shared authored normal) placed by N node transforms. Each AddRigidMesh
        // call with the SAME mesh builder adds another instance (node) - the multi-node instancing case.
        static string WriteTriangleGlb((Vector3 p0, Vector3 p1, Vector3 p2) tri, Vector3 normal, params Matrix4x4[] placements)
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty>("tri");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexEmpty> V(Vector3 p) =>
                new(new VertexPositionNormal(p, normal));
            prim.AddTriangle(V(tri.p0), V(tri.p1), V(tri.p2));

            var scene = new SceneBuilder();
            foreach (var m in placements) scene.AddRigidMesh(mesh, m);

            string path = Path.Combine(Path.GetTempPath(), $"ke_node_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        static (Vector3 p0, Vector3 p1, Vector3 p2) UnitTri =>
            (new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0));

        [Fact]
        public void TranslatedNode_PlacesGeometryAtTransformedPosition()
        {
            string path = WriteTriangleGlb(UnitTri, Vector3.UnitZ, Matrix4x4.CreateTranslation(10, 0, 0));
            try
            {
                var mesh = GltfLoader.Load(path);
                Assert.Equal(3, mesh.Vertices.Length);
                float minX = mesh.Vertices.Min(v => v.Position.X);
                // Old (broken) loader ignored the node => geometry at x~0; baked => the triangle starts at x=10.
                Assert.True(minX > 9.9f, $"expected translated geometry near x=10, got minX={minX}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RotatedNode_TransformsNormals()
        {
            // A +Z normal rotated +90 deg about X maps to -Y. (For a pure rotation the normal matrix == R.)
            string path = WriteTriangleGlb(UnitTri, Vector3.UnitZ, Matrix4x4.CreateRotationX(MathF.PI / 2f));
            try
            {
                var mesh = GltfLoader.Load(path);
                foreach (var v in mesh.Vertices)
                    Assert.True(Vector3.Distance(v.Normal, new Vector3(0, -1, 0)) < 1e-3f,
                        $"expected normal ~ (0,-1,0), got {v.Normal}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NonUniformScale_UsesNormalMatrixNotWorldMatrix()
        {
            // A 45-degree normal in XY under a 2x stretch on X. The normal matrix (inverse-transpose) tilts the
            // normal TOWARD Y - the correct result normalize(1,2,0). Naively pushing the normal through the world
            // matrix would tilt it toward X - normalize(2,1,0). This test pins the normal-matrix path.
            Vector3 n = Vector3.Normalize(new Vector3(1, 1, 0));
            string path = WriteTriangleGlb(UnitTri, n, Matrix4x4.CreateScale(2, 1, 1));
            try
            {
                var mesh = GltfLoader.Load(path);
                Vector3 expected = Vector3.Normalize(new Vector3(1, 2, 0)); // (0.4472, 0.8944, 0)
                Vector3 wrong = Vector3.Normalize(new Vector3(2, 1, 0));    // (0.8944, 0.4472, 0)
                foreach (var v in mesh.Vertices)
                {
                    Assert.True(Vector3.Distance(v.Normal, expected) < 1e-2f,
                        $"expected normal-matrix result ~ {expected}, got {v.Normal}");
                    Assert.True(Vector3.Distance(v.Normal, wrong) > 0.1f,
                        $"normal matches the raw-world-matrix bug ~ {wrong}: {v.Normal}");
                    Assert.True(v.Normal.Y > v.Normal.X, $"normal should tilt toward Y under an X stretch: {v.Normal}");
                }
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void MeshInstancedByMultipleNodes_EmitsOneCopyPerNode()
        {
            string path = WriteTriangleGlb(UnitTri, Vector3.UnitZ,
                Matrix4x4.CreateTranslation(10, 0, 0), Matrix4x4.CreateTranslation(-10, 0, 0));
            try
            {
                var mesh = GltfLoader.Load(path);
                // Two instances, far apart (no cross-instance weld) => 6 vertices, geometry on both sides.
                Assert.Equal(6, mesh.Vertices.Length);
                Assert.Contains(mesh.Vertices, v => v.Position.X < -9f);
                Assert.Contains(mesh.Vertices, v => v.Position.X > 9f);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void IdentityNode_PassesGeometryThroughUnchanged()
        {
            // Identity world matrix is a no-op: loaded positions are bit-identical to the authored ones (the
            // byte-identical guarantee for pre-baked / single-mesh assets). float32-exact literals (halves).
            var tri = (new Vector3(0.5f, 1.5f, -2.5f), new Vector3(3.5f, 0f, 0f), new Vector3(0f, 4.5f, 0f));
            string path = WriteTriangleGlb(tri, Vector3.UnitZ, Matrix4x4.Identity);
            try
            {
                var mesh = GltfLoader.Load(path);
                var authored = new HashSet<Vector3> { tri.Item1, tri.Item2, tri.Item3 };
                Assert.Equal(3, mesh.Vertices.Length);
                foreach (var v in mesh.Vertices)
                    Assert.Contains(v.Position, authored); // exact equality - no transform applied
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void NodeTransform_EqualsPreBakedGeometry()
        {
            // Baking a node translation must equal authoring the same geometry pre-translated (the conformance
            // property: node placement is geometrically identical to vertex-baked placement).
            string baked = WriteTriangleGlb(
                (new Vector3(10, 0, 0), new Vector3(11, 0, 0), new Vector3(10, 1, 0)), Vector3.UnitZ, Matrix4x4.Identity);
            string viaNode = WriteTriangleGlb(UnitTri, Vector3.UnitZ, Matrix4x4.CreateTranslation(10, 0, 0));
            try
            {
                Vector3[] a = GltfLoader.Load(baked).Vertices.Select(v => v.Position).ToArray();
                Vector3[] b = GltfLoader.Load(viaNode).Vertices.Select(v => v.Position).ToArray();
                Assert.Equal(a.Length, b.Length);
                foreach (Vector3 pb in b)
                    Assert.True(a.Any(pa => Vector3.Distance(pa, pb) < 1e-5f),
                        $"node-baked vertex {pb} has no pre-baked match in [{string.Join(", ", a)}]");
            }
            finally { File.Delete(baked); File.Delete(viaNode); }
        }
    }
}
