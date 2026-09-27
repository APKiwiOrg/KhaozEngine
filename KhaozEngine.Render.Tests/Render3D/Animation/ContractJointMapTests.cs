using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class ContractJointMapTests
    {
        const float Epsilon = 1e-5f;

        const int HipsNode = 1, SpineNode = 2, HeadNode = 3, LegNode = 4, SocketNode = 5;
        const int LanternLogical = 16;

        static readonly int[] Parents = { -1, 0, 1, 2, 1, 3, 0 };
        static readonly string[] Names = { "root", "hips", "spine", "head", "leg.L", "socket_head", "lantern" };
        static readonly int[] Logical = { 10, 11, 12, 13, 14, 15, 16 };

        // Skin bones in an order unlike the node order, so a node read as a bone shows. The lantern is a named
        // node outside the contract and outside the skin.
        static readonly int[] Skin = { HeadNode, HipsNode, SpineNode, SocketNode, LegNode };

        static readonly SkeletonContract Contract = new(new[]
        {
            new ContractJoint("root", null, false),
            new ContractJoint("hips", "root", true),
            new ContractJoint("spine", "hips", true),
            new ContractJoint("head", "spine", true),
            new ContractJoint("leg.L", "hips", true),
            new ContractJoint("socket_head", "head", false),
        });

        [Fact]
        public void ResolvesEveryContractJointByNodeAndBone()
        {
            Skeleton skeleton = Build();
            var map = new ContractJointMap(skeleton, Contract);

            Assert.Same(skeleton, map.Skeleton);
            Assert.Same(Contract, map.Contract);
            foreach (ContractJoint joint in Contract.Joints)
                Assert.Equal(skeleton.IndexOf(joint.Name), map.Node(joint.Name));
            Assert.Equal(0, map.Bone("head"));
            Assert.Equal(1, map.Bone("hips"));
            Assert.Equal(2, map.Bone("spine"));
            Assert.Equal(3, map.Bone("socket_head"));
            Assert.Equal(4, map.Bone("leg.L"));

            var outside = Assert.Throws<InvalidOperationException>(() => map.Bone("root"));
            Assert.Contains("'root'", outside.Message, StringComparison.Ordinal);
            var unknown = Assert.Throws<KeyNotFoundException>(() => map.Node("tail"));
            Assert.Contains("'tail'", unknown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesAMissingJointNamingIt()
        {
            var longer = new List<ContractJoint>(Contract.Joints) { new("tail", "hips", true) };
            var extra = Assert.Throws<ArgumentException>(
                () => new ContractJointMap(Build(), new SkeletonContract(longer)));
            Assert.Contains("contract joint 'tail'", extra.Message, StringComparison.Ordinal);

            string[] names = (string[])Names.Clone();
            names[HeadNode] = "skull";
            var renamed = Assert.Throws<ArgumentException>(() => new ContractJointMap(Build(names: names), Contract));
            Assert.Contains("contract joint 'head'", renamed.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesAMisparentedJointNamingBothParents()
        {
            int[] parents = (int[])Parents.Clone();
            parents[LegNode] = SpineNode;

            var error = Assert.Throws<ArgumentException>(
                () => new ContractJointMap(Build(parents: parents), Contract));

            Assert.Contains("'leg.L'", error.Message, StringComparison.Ordinal);
            Assert.Contains("parent 'spine'", error.Message, StringComparison.Ordinal);
            Assert.Contains("expected 'hips'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesDuplicateNodeNames()
        {
            string[] names = (string[])Names.Clone();
            names[6] = "spine";

            var error = Assert.Throws<ArgumentException>(() => new ContractJointMap(Build(names: names), Contract));

            Assert.Contains("Duplicate contract joint 'spine'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesANonRootJointOutsideTheSkin()
        {
            var error = Assert.Throws<ArgumentException>(() => new ContractJointMap(
                Build(skin: new[] { HeadNode, HipsNode, SpineNode, SocketNode }), Contract));

            Assert.Contains("'leg.L'", error.Message, StringComparison.Ordinal);
            Assert.Contains("not in the skin", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void RefusesMoreNodesThanASkinnedDrawTakes()
        {
            int count = SkinningMath.MaxBonesPerDraw + 1;
            var parents = new int[count];
            var rest = new JointPose[count];
            var logical = new int[count];
            var names = new string[count];
            var skin = new int[count - 1];
            for (int node = 0; node < count; node++)
            {
                parents[node] = node - 1;
                rest[node] = JointPose.Identity;
                logical[node] = node;
                names[node] = node == 0 ? "root" : $"link_{node}";
                if (node > 0) skin[node - 1] = node;
            }
            var contract = new SkeletonContract(new[] { new ContractJoint("root", null, false) });

            var error = Assert.Throws<ArgumentException>(
                () => new ContractJointMap(new Skeleton(parents, rest, logical, skin, names), contract));

            Assert.Contains($"{count} nodes", error.Message, StringComparison.Ordinal);
            Assert.Contains($"{SkinningMath.MaxBonesPerDraw}", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void WithoutAStanceTheBaseIsTheBindRest()
        {
            Skeleton skeleton = Build();
            var map = new ContractJointMap(skeleton, Contract);

            Assert.Equal(skeleton.RestLocal, map.BaseLocal.ToArray());
            AssertFramesFollow(map, skeleton.ComposeRestPose());
        }

        [Fact]
        public void AStanceIsSampledOnceAsTheBase()
        {
            Skeleton skeleton = Build();
            AnimationClip stance = Clip("stance",
                Move(Logical[HipsNode], new Vector3(0f, 1f, 0.02f)),
                Turn(Logical[SpineNode], Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f)));

            var map = new ContractJointMap(skeleton, Contract, stance);

            JointPose[] expected = AnimationSampler.SamplePose(stance, skeleton, 0f);
            Assert.NotEqual(skeleton.RestLocal[HipsNode], expected[HipsNode]);
            Assert.NotEqual(skeleton.RestLocal[SpineNode], expected[SpineNode]);
            Assert.Equal(expected, map.BaseLocal.ToArray());
            var palette = new Matrix4x4[skeleton.BoneCount];
            skeleton.ComposeInto(expected, palette);
            AssertFramesFollow(map, palette);

            // The stance, not the bind, is what the skin deforms to: a vertex held by the hips rises with them.
            SkinnedGltfMesh mesh = Mesh(skeleton);
            Vector3[] positions = map.SkinAtBase(mesh);
            AssertNear(mesh.Vertices[1].Position + new Vector3(0f, 0.1f, 0.02f), positions[1], "the hips vertex");
        }

        [Fact]
        public void RefusesAStanceUnderAnotherName()
        {
            Skeleton skeleton = Build();
            AnimationClip idle = Clip("idle", Turn(Logical[HeadNode], Quaternion.Identity));

            var error = Assert.Throws<ArgumentException>(() => new ContractJointMap(skeleton, Contract, idle));
            Assert.Contains("'idle'", error.Message, StringComparison.Ordinal);
            Assert.Contains("'stance'", error.Message, StringComparison.Ordinal);

            AnimationClip stance = Clip("stance", Turn(Logical[HeadNode], Quaternion.Identity));
            var renamed = Assert.Throws<ArgumentException>(
                () => new ContractJointMap(skeleton, Contract, stance, stanceClipName: "rest_pose"));
            Assert.Contains("'stance'", renamed.Message, StringComparison.Ordinal);
            Assert.Contains("'rest_pose'", renamed.Message, StringComparison.Ordinal);

            AnimationClip restPose = Clip("rest_pose", Turn(Logical[HeadNode], Quaternion.Identity));
            var map = new ContractJointMap(skeleton, Contract, restPose, stanceClipName: "rest_pose");
            Assert.Equal(AnimationSampler.SamplePose(restPose, skeleton, 0f), map.BaseLocal.ToArray());
        }

        [Fact]
        public void RefusesAStanceKeyingATrackTwice()
        {
            Skeleton skeleton = Build();
            int head = Logical[HeadNode];
            var turned = new JointTrack(head)
            {
                Rotation = new QuaternionTrack(new[] { 0f, 1f }, new[] { Quaternion.Identity, Quaternion.Identity },
                    InterpolationMode.Linear),
            };
            var moved = new JointTrack(head)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.UnitY },
                    InterpolationMode.Linear),
            };
            var unkeyed = new JointTrack(head)
            {
                Rotation = new QuaternionTrack(Array.Empty<float>(), Array.Empty<Quaternion>(), InterpolationMode.Step),
            };

            var cases = new[] { (turned, "2 times"), (moved, "2 times"), (unkeyed, "0 times") };
            foreach ((JointTrack track, string times) in cases)
            {
                var error = Assert.Throws<ArgumentException>(
                    () => new ContractJointMap(skeleton, Contract, Clip("stance", track)));

                Assert.Contains("'head'", error.Message, StringComparison.Ordinal);
                Assert.Contains(times, error.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void RefusesAStanceOnAJointOutsideTheContract()
        {
            Skeleton skeleton = Build();
            Assert.Equal(6, skeleton.NodeForLogicalIndex(LanternLogical));
            Assert.Equal(-1, skeleton.NodeForLogicalIndex(99));

            var cases = new[] { (LanternLogical, "glTF node 16 'lantern'"), (99, "glTF node 99") };
            foreach ((int logical, string named) in cases)
            {
                var error = Assert.Throws<ArgumentException>(
                    () => new ContractJointMap(skeleton, Contract, Clip("stance", Turn(logical, Quaternion.Identity))));

                Assert.Contains(named, error.Message, StringComparison.Ordinal);
                Assert.Contains("not a contract joint", error.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void RefusesAStanceWithAScaleTrack()
        {
            var track = new JointTrack(Logical[SpineNode])
            {
                Scale = new Vector3Track(new[] { 0f }, new[] { new Vector3(1.1f) }, InterpolationMode.Step),
            };

            var error = Assert.Throws<ArgumentException>(
                () => new ContractJointMap(Build(), Contract, Clip("stance", track)));

            Assert.Contains("scales 'spine'", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void BodyAlignmentIsTheIdentityOnABodyAxesJointAndCancelsATurnedRest()
        {
            Skeleton skeleton = Build();
            var map = new ContractJointMap(skeleton, Contract);

            AssertMatrixNear(Matrix4x4.Identity, map.BodyAlignment(HipsNode), "the hips at the body's axes");
            AssertMatrixNear(Matrix4x4.Identity, map.BodyAlignment(SpineNode), "the spine at the body's axes");
            foreach (int node in new[] { HeadNode, LegNode, SocketNode })
                AssertCancels(map, node);

            // A stance that turns the hips turns every joint below them, and the alignment follows the stance.
            AnimationClip stance = Clip("stance",
                Turn(Logical[HipsNode], Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f)));
            var turned = new ContractJointMap(skeleton, Contract, stance);
            foreach (int node in new[] { HipsNode, SpineNode, HeadNode, LegNode, SocketNode })
                AssertCancels(turned, node);
            Assert.False(IsNear(Matrix4x4.Identity, turned.BodyAlignment(HipsNode)),
                "The stance's turn must show in the hips' alignment.");
        }

        [Fact]
        public void SkinAtBaseWithoutAStanceIsTheSkinsOwnPositions()
        {
            Skeleton skeleton = Build();
            var map = new ContractJointMap(skeleton, Contract);
            SkinnedGltfMesh mesh = Mesh(skeleton);

            Vector3[] positions = map.SkinAtBase(mesh);

            Assert.Equal(mesh.Vertices.Length, positions.Length);
            for (int i = 0; i < positions.Length; i++)
                AssertNear(mesh.Vertices[i].Position, positions[i], $"vertex {i}");

            SkinnedGltfMesh other = Mesh(Build());
            var error = Assert.Throws<ArgumentException>(() => map.SkinAtBase(other));
            Assert.Contains("same skeleton", error.Message, StringComparison.Ordinal);
        }

        // Every contract joint's base frame is the palette's, and its parent base inverse takes that frame back to
        // the joint's base local.
        static void AssertFramesFollow(ContractJointMap map, Matrix4x4[] palette)
        {
            Skeleton skeleton = map.Skeleton;
            foreach (ContractJoint joint in map.Contract.Joints)
            {
                int node = map.Node(joint.Name);
                int bone = skeleton.BoneIndexOfNode(node);
                Matrix4x4 expected = bone >= 0 ? palette[bone] : map.BaseLocal[node].ToMatrix();
                AssertMatrixNear(expected, map.BaseWorld(node), $"{joint.Name}'s base frame");
                AssertMatrixNear(map.BaseLocal[node].ToMatrix(), map.BaseWorld(node) * map.ParentBaseInverse(node),
                    $"{joint.Name}'s base local");
            }
        }

        // A piece at the identity, aligned and carried through the joint's rigid base frame, sits at the joint with
        // the body's axes. The alignment is a pure rotation.
        static void AssertCancels(ContractJointMap map, int node)
        {
            Matrix4x4 baseWorld = map.BaseWorld(node);
            Matrix4x4 alignment = map.BodyAlignment(node);
            Matrix4x4 placed = alignment * BoneSocket.ComposeRigid(Matrix4x4.Identity, baseWorld, Matrix4x4.Identity);

            AssertMatrixNear(Matrix4x4.CreateTranslation(baseWorld.Translation), placed, $"node {node} aligned");
            AssertNear(Vector3.Zero, alignment.Translation, $"node {node}'s alignment translation");
        }

        static Skeleton Build(int[]? parents = null, string[]? names = null, int[]? skin = null) =>
            new(
                parents ?? (int[])Parents.Clone(),
                new[]
                {
                    JointPose.Identity,
                    Pose(new Vector3(0f, 0.9f, 0f), Quaternion.Identity),
                    Pose(new Vector3(0f, 0.25f, -0.05f), Quaternion.Identity),
                    Pose(new Vector3(0f, 0.3f, 0.2f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f)),
                    Pose(new Vector3(0.2f, -0.1f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI)),
                    Pose(new Vector3(0f, 0.1f, 0.05f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f)),
                    Pose(new Vector3(0.5f, 1f, 0f), Quaternion.Identity),
                },
                (int[])Logical.Clone(),
                skin ?? (int[])Skin.Clone(),
                names ?? (string[])Names.Clone());

        // One vertex held wholly by each bone, then one shared between the hips and the leg. Vertex 1 is the hips'.
        static SkinnedGltfMesh Mesh(Skeleton skeleton)
        {
            Matrix4x4[] rest = skeleton.ComposeRestPose();
            var inverseBind = new Matrix4x4[rest.Length];
            for (int bone = 0; bone < rest.Length; bone++)
                Assert.True(Matrix4x4.Invert(rest[bone], out inverseBind[bone]));

            var vertices = new[]
            {
                Vertex(new Vector3(0.05f, 1.5f, 0.2f), new Vector4(0f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(new Vector3(0.1f, 0.85f, -0.1f), new Vector4(1f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(new Vector3(-0.1f, 1.2f, 0f), new Vector4(2f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(new Vector3(0f, 1.55f, 0.3f), new Vector4(3f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(new Vector3(0.22f, 0.4f, 0.03f), new Vector4(4f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(new Vector3(0.18f, 0.75f, 0f), new Vector4(1f, 4f, 0f, 0f), new Vector4(0.5f, 0.5f, 0f, 0f)),
            };
            return new SkinnedGltfMesh(vertices, new ushort[] { 0, 1, 2, 3, 4, 5 }, inverseBind, rest, skeleton);
        }

        static SkinnedVertex Vertex(Vector3 position, Vector4 bones, Vector4 weights) =>
            new()
            {
                Position = position,
                Normal = Vector3.UnitY,
                Color = Vector4.One,
                BoneIndices = bones,
                BoneWeights = weights,
            };

        static AnimationClip Clip(string name, params JointTrack[] tracks) => new(name, 0f, tracks);

        static JointTrack Turn(int logical, Quaternion rotation) =>
            new(logical) { Rotation = new QuaternionTrack(new[] { 0f }, new[] { rotation }, InterpolationMode.Step) };

        static JointTrack Move(int logical, Vector3 translation) =>
            new(logical)
            {
                Translation = new Vector3Track(new[] { 0f }, new[] { translation }, InterpolationMode.Step),
            };

        static JointPose Pose(Vector3 translation, Quaternion rotation) =>
            new() { Translation = translation, Rotation = rotation, Scale = Vector3.One };

        static bool IsNear(Matrix4x4 expected, Matrix4x4 actual)
        {
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    if (MathF.Abs(expected[row, column] - actual[row, column]) > Epsilon) return false;
            return true;
        }

        static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual, string what) =>
            Assert.True(IsNear(expected, actual), $"{what}: expected {expected}, got {actual}.");

        static void AssertNear(Vector3 expected, Vector3 actual, string what) =>
            Assert.True(Vector3.Distance(expected, actual) <= Epsilon, $"{what}: expected {expected}, got {actual}.");
    }
}
