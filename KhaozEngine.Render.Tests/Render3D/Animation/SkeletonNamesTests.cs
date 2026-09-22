using System;
using System.IO;
using System.Numerics;
using KhaozEngine.Render3D;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class SkeletonNamesTests
    {
        static Skeleton NamedSkeleton() =>
            new(
                new[] { -1, 0, 1, 1 },
                new[] { JointPose.Identity, JointPose.Identity, JointPose.Identity, JointPose.Identity },
                new[] { 10, 11, 12, 13 },
                new[] { 1, 3 },
                new[] { "Armature", "Hips", "Spine", "Hand.R" });

        static string WriteNamedRiggedGlb()
        {
            var mesh = new MeshBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4>("skin");
            var prim = mesh.UsePrimitive(MaterialBuilder.CreateDefault());

            VertexBuilder<VertexPositionNormal, VertexEmpty, VertexJoints4> V(Vector3 position, int bone) =>
                new(new VertexPositionNormal(position, Vector3.UnitZ), default, new VertexJoints4((bone, 1f)));

            prim.AddTriangle(
                V(new Vector3(0, 0, 0), 0),
                V(new Vector3(0, 1, 0), 1),
                V(new Vector3(1, 1, 0), 1));

            var armature = new NodeBuilder("Armature");
            var hips = armature.CreateNode("Hips");
            var hand = hips.CreateNode("Hand.R");
            hand.LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0);
            hand.CreateNode("weapon_socket");

            var scene = new SceneBuilder();
            scene.AddSkinnedMesh(mesh, Matrix4x4.Identity, hips, hand);
            string path = Path.Combine(Path.GetTempPath(), $"ke_named_skin_{Guid.NewGuid():N}.glb");
            scene.ToGltf2().SaveGLB(path);
            return path;
        }

        [Fact]
        public void LoadSkinned_RoundTripsSkeletonNodeNamesInNodeOrder()
        {
            string path = WriteNamedRiggedGlb();
            try
            {
                Skeleton skeleton = Assert.IsType<Skeleton>(GltfLoader.LoadSkinned(path).Skeleton);

                Assert.Equal(new[] { "Armature", "Hips", "Hand.R" }, skeleton.NodeNames);
                Assert.Equal(0, skeleton.IndexOf("Armature"));
                Assert.Equal(1, skeleton.IndexOf("Hips"));
                Assert.Equal(2, skeleton.IndexOf("Hand.R"));
                Assert.True(skeleton.TryIndexOf("Hand.R", out int hand));
                Assert.Equal(2, hand);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void LoadSkinned_OmitsChildOutsideSkinJoints()
        {
            string path = WriteNamedRiggedGlb();
            try
            {
                ModelRoot source = ModelRoot.Load(path);
                Assert.Contains(source.LogicalNodes, node => node.Name == "weapon_socket");

                Skeleton skeleton = Assert.IsType<Skeleton>(GltfLoader.LoadSkinned(path).Skeleton);
                Assert.DoesNotContain("weapon_socket", skeleton.NodeNames);
                Assert.False(skeleton.TryIndexOf("weapon_socket", out _));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void IndexOf_MissingNameListsMissingAndKnownNames()
        {
            Skeleton skeleton = NamedSkeleton();

            ArgumentException error = Assert.Throws<ArgumentException>(() => skeleton.IndexOf("Head"));

            Assert.Contains("Head", error.Message, StringComparison.Ordinal);
            Assert.Contains("Armature", error.Message, StringComparison.Ordinal);
            Assert.Contains("Hips", error.Message, StringComparison.Ordinal);
            Assert.Contains("Spine", error.Message, StringComparison.Ordinal);
            Assert.Contains("Hand.R", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void IndexOf_DuplicateNameThrowsAtLookup()
        {
            var skeleton = new Skeleton(
                new[] { -1, 0, 1 },
                new[] { JointPose.Identity, JointPose.Identity, JointPose.Identity },
                new[] { 10, 11, 12 },
                new[] { 0, 1, 2 },
                new[] { "root", "arm", "arm" });

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => skeleton.IndexOf("arm"));

            Assert.Contains("arm", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BoneIndexOfNode_InvertsJointToNodeAndRejectsNonJoints()
        {
            Skeleton skeleton = NamedSkeleton();

            Assert.Equal(-1, skeleton.BoneIndexOfNode(0));
            Assert.Equal(0, skeleton.BoneIndexOfNode(1));
            Assert.Equal(-1, skeleton.BoneIndexOfNode(2));
            Assert.Equal(1, skeleton.BoneIndexOfNode(3));
        }

        [Fact]
        public void NamesFreeConstructor_ReportsEmptyNames()
        {
            var skeleton = new Skeleton(
                new[] { -1, 0 },
                new[] { JointPose.Identity, JointPose.Identity },
                new[] { 10, 11 },
                new[] { 0, 1 });

            Assert.Equal(new[] { string.Empty, string.Empty }, skeleton.NodeNames);
            Assert.False(skeleton.TryIndexOf("root", out int node));
            Assert.Equal(-1, node);
        }

        [Fact]
        public void Subtree_NameFromSkeletonMatchesExplicitNamesOverload()
        {
            Skeleton skeleton = NamedSkeleton();

            BoneMask expected = BoneMask.Subtree(skeleton, "Hips", skeleton.NodeNames, 0.5f);
            BoneMask actual = BoneMask.Subtree(skeleton, "Hips", 0.5f);

            for (int node = 0; node < skeleton.NodeCount; node++)
                Assert.Equal(expected.Weight(node), actual.Weight(node));
        }
    }
}
