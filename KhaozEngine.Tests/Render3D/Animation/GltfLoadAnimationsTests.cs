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

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class GltfLoadAnimationsTests
    {
        // A 2-bone skinned triangle (bone0 at origin, bone1 a child at +1 Y) WITH a "walk" animation that
        // translates bone1 from (0,1,0) to (0,3,0) over 1 second.
        static string WriteAnimatedRiggedGlb()
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4>("skin");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());
            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4> V(Vector3 p, int bone) =>
                new(new VertexPositionNormal(p, Vector3.UnitZ), default, new VertexJoints4((bone, 1f)));
            prim.AddTriangle(
                V(new Vector3(0, 0, 0), 0),
                V(new Vector3(0, 1, 0), 1),
                V(new Vector3(1, 1, 0), 1));

            var bone0 = new NodeBuilder("bone0");
            var bone1 = bone0.CreateNode("bone1");
            bone1.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);
            // Animation track "walk": bone1 translates (0,1,0) -> (0,3,0) over 1s.
            bone1.UseTranslation("walk")
                 .WithPoint(0f, new Vector3(0, 1, 0))
                 .WithPoint(1f, new Vector3(0, 3, 0));

            var scene = new SceneBuilder();
            scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, bone0, bone1);
            var model = scene.ToGltf2();

            string path = Path.Combine(Path.GetTempPath(), $"ke_anim_{Guid.NewGuid():N}.glb");
            model.SaveGLB(path);
            return path;
        }

        [Fact]
        public void LoadSkinned_AttachesSkeleton_RestPoseMatchesComposed()
        {
            string path = WriteAnimatedRiggedGlb();
            try
            {
                SkinnedGltfMesh m = GltfLoader.LoadSkinned(path);
                Assert.NotNull(m.Skeleton);
                Skeleton s = m.Skeleton!;
                Assert.Equal(m.BoneCount, s.BoneCount);
                Matrix4x4[] composed = s.ComposeRestPose();
                for (int b = 0; b < m.BoneCount; b++)
                    Assert.True(Vector3.Distance(composed[b].Translation, m.RestPose[b].Translation) < 1e-3f,
                        $"bone {b}: composed {composed[b].Translation} vs rest {m.RestPose[b].Translation}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadAnimations_ReadsClipWithTrsTracksAndDuration()
        {
            string path = WriteAnimatedRiggedGlb();
            try
            {
                IReadOnlyList<AnimationClip> clips = GltfLoader.LoadAnimations(path);
                Assert.Single(clips);
                AnimationClip clip = clips[0];
                Assert.Equal("walk", clip.Name);
                Assert.True(MathF.Abs(clip.Duration - 1f) < 1e-3f, clip.Duration.ToString());
                Assert.NotEmpty(clip.Tracks);
                Assert.Contains(clip.Tracks, t => t.Translation != null);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LoadAnimations_SampledClip_PosesTheHierarchy()
        {
            string path = WriteAnimatedRiggedGlb();
            try
            {
                SkinnedGltfMesh m = GltfLoader.LoadSkinned(path);
                AnimationClip clip = GltfLoader.LoadAnimations(path).Single();
                // At t=0.5 bone1's local translation is (0,2,0); its parent (bone0) is at origin -> world (0,2,0).
                Matrix4x4[] palette = AnimationSampler.SampleToBonePalette(clip, m.Skeleton!, 0.5f);
                Assert.True(Vector3.Distance(palette[1].Translation, new Vector3(0, 2, 0)) < 1e-3f,
                    palette[1].Translation.ToString());
            }
            finally { File.Delete(path); }
        }
    }
}
