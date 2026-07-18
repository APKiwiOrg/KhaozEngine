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
    public class GltfLoaderSkinnedTests
    {
        // Build a minimal 2-bone skinned triangle glb in a temp file and return its path.
        static string WriteRiggedGlb()
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4>("skin");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());

            // Three verts; vert at base bound to bone 0, the other two to bone 1.
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4> V(Vector3 p, int bone) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), default, new VertexJoints4((bone, 1f)));

            prim.AddTriangle(
                V(new Vector3(0, 0, 0), 0),
                V(new Vector3(0, 1, 0), 1),
                V(new Vector3(1, 1, 0), 1));

            // Armature: bone0 at origin, bone1 a child translated +1 in Y (rest world = (0,1,0)).
            var bone0 = new NodeBuilder("bone0");
            var bone1 = bone0.CreateNode("bone1");
            bone1.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);

            var scene = new SceneBuilder();
            scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone0, bone1);
            var model = scene.ToGltf2();

            string path = Path.Combine(Path.GetTempPath(), $"ke_skin_{Guid.NewGuid():N}.glb");
            model.SaveGLB(path);
            return path;
        }

        [Fact]
        public void LoadSkinned_ReadsBonesWeightsAndInverseBind()
        {
            string path = WriteRiggedGlb();
            try
            {
                SkinnedGltfMesh m = GltfLoader.LoadSkinned(path);

                Assert.Equal(2, m.BoneCount);
                Assert.True(m.Vertices.Length >= 3);
                foreach (var v in m.Vertices)
                {
                    float sum = v.BoneWeights.X + v.BoneWeights.Y + v.BoneWeights.Z + v.BoneWeights.W;
                    Assert.True(MathF.Abs(sum - 1f) < 1e-3f);
                }
                // bone1's inverse-bind should translate model->bone-local by -1 in Y (inverse of its (0,1,0) rest).
                Vector3 ibTranslation = m.InverseBind[1].Translation;
                Assert.True(MathF.Abs(ibTranslation.Y + 1f) < 1e-3f, $"expected ~ -1 Y, got {ibTranslation}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadSkinned_RestPose_LeavesGeometryUnmoved()
        {
            string path = WriteRiggedGlb();
            try
            {
                SkinnedGltfMesh m = GltfLoader.LoadSkinned(path);
                Span<Matrix4x4> composed = stackalloc Matrix4x4[m.BoneCount];
                for (int i = 0; i < m.BoneCount; i++)
                    composed[i] = SkinningMath.Compose(m.RestPose[i], m.InverseBind[i]);
                foreach (var v in m.Vertices)
                {
                    var skin = SkinningMath.BlendSkinMatrix(composed, v.BoneIndices, v.BoneWeights);
                    Assert.True(Vector3.Distance(Vector3.Transform(v.Position, skin), v.Position) < 1e-3f);
                }
            }
            finally { File.Delete(path); }
        }

        // A rigged triangle WITH UVs (no authored TANGENT) so the loader computes a per-vertex tangent from
        // UV+position. The computed tangents must be finite and either zero or unit (the model shader's
        // contract), and at least one vertex carries a real tangent (the triangle has a UV gradient).
        [Fact]
        public void LoadSkinned_ComputesTangentsFromUvWhenAbsent()
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>("uvskin");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());

            VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4> V(Vector3 p, Vector2 uv, int bone) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), new VertexTexture1(uv), new VertexJoints4((bone, 1f)));

            prim.AddTriangle(
                V(new Vector3(0, 0, 0), new Vector2(0, 0), 0),
                V(new Vector3(1, 0, 0), new Vector2(1, 0), 1),
                V(new Vector3(0, 1, 0), new Vector2(0, 1), 1));

            var bone0 = new NodeBuilder("bone0");
            var bone1 = bone0.CreateNode("bone1");
            bone1.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);
            var scene = new SceneBuilder();
            scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone0, bone1);
            string path = Path.Combine(Path.GetTempPath(), $"ke_uvskin_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            try
            {
                SkinnedGltfMesh m = GltfLoader.LoadSkinned(path);
                int real = 0;
                foreach (var v in m.Vertices)
                {
                    var t = new Vector3(v.Tangent.X, v.Tangent.Y, v.Tangent.Z);
                    Assert.True(float.IsFinite(t.X) && float.IsFinite(t.Y) && float.IsFinite(t.Z) && float.IsFinite(v.Tangent.W));
                    float len = t.Length();
                    Assert.True(len < 1e-4f || (len > 0.99f && len < 1.01f), $"tangent neither zero nor unit: {len}");
                    if (len > 0.99f) { real++; Assert.True(v.Tangent.W == 1f || v.Tangent.W == -1f); }
                }
                Assert.True(real > 0, "a UV-bearing triangle should yield at least one real tangent");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadSkinned_OnUnriggedMesh_Throws()
        {
            var mesh = new MeshBuilder<VertexPosition, VertexEmpty, VertexEmpty>("plain");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());
            prim.AddTriangle(
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(0, 0, 0)),
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(1, 0, 0)),
                new VertexBuilder<VertexPosition, VertexEmpty, VertexEmpty>(new VertexPosition(0, 1, 0)));
            var scene = new SceneBuilder();
            scene.AddRigidMesh(mesh, Matrix4x4.Identity);
            string path = Path.Combine(Path.GetTempPath(), $"ke_plain_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            try { Assert.Throws<InvalidOperationException>(() => GltfLoader.LoadSkinned(path)); }
            finally { File.Delete(path); }
        }
    }
}
