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

            var root = Assert.Throws<ContractJointMapStateException>(() => map.Bone("root"));
            Assert.Equal(ContractJointMapRefusal.NamedNodeOutsideSkin, root.Reason);
            Assert.Equal("root", root.JointName);
            Assert.IsAssignableFrom<InvalidOperationException>(root);
            var tail = Assert.Throws<ContractJointMapKeyNotFoundException>(() => map.Node("tail"));
            Assert.Equal(ContractJointMapRefusal.NodeNameMissing, tail.Reason);
            Assert.Equal("tail", tail.JointName);
            Assert.IsAssignableFrom<KeyNotFoundException>(tail);
        }

        [Fact]
        public void RefusesAMissingJointNamingIt()
        {
            var longer = new List<ContractJoint>(Contract.Joints) { new("tail", "hips", true) };
            var extra = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(Build(), new SkeletonContract(longer)));
            Assert.Equal(ContractJointMapRefusal.ContractJointMissing, extra.Reason);
            Assert.Equal("tail", extra.JointName);
            Assert.Equal("skeleton", extra.ParamName);
            Assert.IsAssignableFrom<ArgumentException>(extra);

            string[] names = (string[])Names.Clone();
            names[HeadNode] = "skull";
            var renamed = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(Build(names: names), Contract));
            Assert.Equal(ContractJointMapRefusal.ContractJointMissing, renamed.Reason);
            Assert.Equal("head", renamed.JointName);
            _ = new ContractJointMap(Build(), Contract);
        }

        [Fact]
        public void RefusesAMisparentedJointNamingBothParents()
        {
            int[] parents = (int[])Parents.Clone();
            parents[LegNode] = SpineNode;

            var error = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(Build(parents: parents), Contract));

            Assert.Equal(ContractJointMapRefusal.ParentNameMismatch, error.Reason);
            Assert.Equal("leg.L", error.JointName);
            Assert.Equal("spine", error.ActualParentName);
            Assert.Equal("hips", error.ExpectedParentName);
            _ = new ContractJointMap(Build(), Contract);
        }

        [Theory]
        [InlineData(-2)]
        [InlineData(7)]
        public void RefusesAParentIndexOutsideTheSkeleton(int parent)
        {
            int[] parents = (int[])Parents.Clone();
            parents[LegNode] = parent;
            var error = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(Build(parents: parents), Contract));
            Assert.Equal(ContractJointMapRefusal.ParentIndexInvalid, error.Reason);
            Assert.Equal("leg.L", error.JointName);
            Assert.Equal(parent, error.ParentIndex);
            Assert.Equal(7, error.NodeCount);
            Assert.Equal("skeleton", error.ParamName);
            _ = new ContractJointMap(Build(), Contract);
        }

        [Theory]
        [InlineData(10000f, ContractJointMapRefusal.BaseFrameSingular, "root")]
        [InlineData(1000f, ContractJointMapRefusal.ParentBaseFrameSingular, "child")]
        public void RefusesSingularBaseFrames(float scale, ContractJointMapRefusal reason, string joint)
        {
            // Inject only the inversion result, without depending on floating-point cancellation.
            Action<bool> refuseSingularFrame = reason == ContractJointMapRefusal.ParentBaseFrameSingular
                ? inverted => ContractJointMap.RefuseSingularParentBaseFrame(inverted, joint, 0)
                : inverted => ContractJointMap.RefuseSingularBaseFrame(inverted, joint, 0);

            var error = Assert.Throws<ContractJointMapArgumentException>(() => refuseSingularFrame(false));
            Assert.Equal(reason, error.Reason);
            Assert.Equal(joint, error.JointName);
            Assert.Equal("skeleton", error.ParamName);
            if (reason == ContractJointMapRefusal.BaseFrameSingular)
                Assert.Equal(0, error.NodeIndex);
            else
                Assert.Equal(0, error.ParentNodeIndex);

            refuseSingularFrame(true);
            var contract = new SkeletonContract([new("root", null, false), new("child", "root", true)]);
            var pose = JointPose.Identity;
            pose.Scale = new Vector3(scale);
            _ = new ContractJointMap(new Skeleton([-1, 0], [pose, JointPose.Identity], [0, 1], [1],
                ["root", "child"]), contract);
        }

        [Fact]
        public void RefusesDuplicateNodeNames()
        {
            string[] joint = (string[])Names.Clone();
            joint[6] = "spine";
            var onAJoint = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(Build(names: joint), Contract));
            Assert.Equal(ContractJointMapRefusal.DuplicateNodeName, onAJoint.Reason);
            Assert.Equal("spine", onAJoint.NodeName);
            Assert.Equal(6, onAJoint.NodeIndex);
            _ = new ContractJointMap(Build(), Contract);

            // A name the contract never mentions is still ambiguous, and the refusal says it is a node's name.
            Skeleton plain = Build();
            var twoLanterns = new Skeleton(
                [.. plain.ParentIndices, 0],
                [.. plain.RestLocal, JointPose.Identity],
                [.. plain.NodeLogicalIndex, 17],
                (int[])plain.JointToNode.Clone(),
                [.. plain.NodeNames, "lantern"]);
            var offTheContract = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(twoLanterns, Contract));
            Assert.Equal(ContractJointMapRefusal.DuplicateNodeName, offTheContract.Reason);
            Assert.Equal("lantern", offTheContract.NodeName);
            Assert.Equal(7, offTheContract.NodeIndex);
            _ = new ContractJointMap(new Skeleton(twoLanterns.ParentIndices, twoLanterns.RestLocal,
                twoLanterns.NodeLogicalIndex, twoLanterns.JointToNode, [.. plain.NodeNames, "torch"]), Contract);
            Assert.DoesNotContain("contract joint", offTheContract.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void LeavesUnnamedNodesAlone()
        {
            Skeleton plain = Build();
            Skeleton helpers = new(
                [.. plain.ParentIndices, HipsNode, 0],
                [.. plain.RestLocal, Pose(new Vector3(0f, 0.1f, 0f), Quaternion.Identity), JointPose.Identity],
                [.. plain.NodeLogicalIndex, 17, 18],
                (int[])plain.JointToNode.Clone(),
                [.. plain.NodeNames, "", ""]);

            var map = new ContractJointMap(helpers, Contract);
            var without = new ContractJointMap(plain, Contract);

            foreach (ContractJoint joint in Contract.Joints)
            {
                int node = map.Node(joint.Name);
                Assert.Equal(without.BaseWorld(node), map.BaseWorld(node));
                Assert.Equal(without.ParentBaseInverse(node), map.ParentBaseInverse(node));
                Assert.Equal(without.BodyAlignment(node), map.BodyAlignment(node));
            }
            Assert.Equal(helpers.RestLocal, map.BaseLocal.ToArray());
            var unnamed = Assert.Throws<ContractJointMapKeyNotFoundException>(() => map.Node(""));
            Assert.Equal(ContractJointMapRefusal.NodeNameMissing, unnamed.Reason);
            Assert.Equal("", unnamed.JointName);
            var helper = Assert.Throws<ContractJointMapArgumentException>(() => map.BaseWorld(7));
            Assert.Equal(ContractJointMapRefusal.NodeOutsideContract, helper.Reason);
            Assert.Equal(7, helper.NodeIndex);
            Assert.Equal("", helper.NodeName);
            Assert.Equal("node", helper.ParamName);
        }

        [Fact]
        public void AnUnnamedSkeletonMissesItsRootRatherThanDuplicatingIt()
        {
            var error = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(Build(names: new string[Names.Length]), Contract));

            Assert.Equal(ContractJointMapRefusal.ContractJointMissing, error.Reason);
            Assert.Equal("root", error.JointName);
            _ = new ContractJointMap(Build(), Contract);
        }

        [Fact]
        public void FrameAccessorsRefuseANonContractNodeAsTheNodeArgument()
        {
            Skeleton skeleton = Build();
            var map = new ContractJointMap(skeleton, Contract);
            int lantern = map.Node("lantern");
            var accessors = new Func<int, Matrix4x4>[] { map.BaseWorld, map.ParentBaseInverse, map.BodyAlignment };

            foreach (Func<int, Matrix4x4> read in accessors)
            {
                var error = Assert.Throws<ContractJointMapArgumentException>(() => read(lantern));
                Assert.Equal(ContractJointMapRefusal.NodeOutsideContract, error.Reason);
                Assert.Equal(lantern, error.NodeIndex);
                Assert.Equal("lantern", error.NodeName);
                Assert.Equal("node", error.ParamName);
                Assert.Throws<ArgumentOutOfRangeException>(() => read(-1));
                Assert.Throws<ArgumentOutOfRangeException>(() => read(skeleton.NodeCount));
            }

            // Alignment keeps the inverse recipe's other fifteen elements, including their rounding and signs.
            var world = new Matrix4x4[skeleton.NodeCount];
            foreach (ContractJoint joint in Contract.Joints)
            {
                int node = map.Node(joint.Name);
                int parent = skeleton.ParentIndices[node];
                Matrix4x4 parentWorld = parent < 0 ? Matrix4x4.Identity : world[parent];
                world[node] = skeleton.RestLocal[node].ToMatrix() * parentWorld;
                Assert.True(Matrix4x4.Invert(parentWorld, out Matrix4x4 parentInverse));
                Matrix4x4 rigid = BoneSocket.ComposeRigid(Matrix4x4.Identity, world[node], Matrix4x4.Identity);
                Assert.True(Matrix4x4.Invert(rigid, out Matrix4x4 inverseRigid));

                Assert.Equal(world[node], map.BaseWorld(node));
                Assert.Equal(parentInverse, map.ParentBaseInverse(node));
                Matrix4x4 expectedAlignment = Matrix4x4.CreateTranslation(rigid.Translation) * inverseRigid;
                Matrix4x4 alignment = map.BodyAlignment(node);
                for (int row = 0; row < 4; row++)
                    for (int column = 0; column < 4; column++)
                        if (row != 3 || column != 3)
                            Assert.Equal(BitConverter.SingleToInt32Bits(expectedAlignment[row, column]),
                                BitConverter.SingleToInt32Bits(alignment[row, column]));
                Assert.Equal(1f, alignment.M44);
            }
        }

        [Fact]
        public void RefusesANonRootJointOutsideTheSkin()
        {
            var error = Assert.Throws<ContractJointMapArgumentException>(() => new ContractJointMap(
                Build(skin: new[] { HeadNode, HipsNode, SpineNode, SocketNode }), Contract));

            Assert.Equal(ContractJointMapRefusal.ContractJointOutsideSkin, error.Reason);
            Assert.Equal("leg.L", error.JointName);
            Assert.Equal(LegNode, error.NodeIndex);
            _ = new ContractJointMap(Build(), Contract);
        }

        [Fact]
        public void RefusesMoreNodesThanASkinnedDrawTakes()
        {
            var contract = new SkeletonContract(new[] { new ContractJoint("root", null, false) });

            // The same chain at the limit is accepted, so the one node more is the only thing refused.
            _ = new ContractJointMap(Chain(SkinningMath.MaxBonesPerDraw), contract);
            Skeleton over = Chain(SkinningMath.MaxBonesPerDraw + 1);

            var error = Assert.Throws<ContractJointMapArgumentException>(() => new ContractJointMap(over, contract));
            Assert.Equal(ContractJointMapRefusal.NodeLimitExceeded, error.Reason);
            Assert.Equal(SkinningMath.MaxBonesPerDraw + 1, error.NodeCount);
            Assert.Equal(SkinningMath.MaxBonesPerDraw, error.MaximumNodeCount);

            static Skeleton Chain(int count)
            {
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
                return new Skeleton(parents, rest, logical, skin, names);
            }
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

            var error = Assert.Throws<ContractJointMapArgumentException>(() => new ContractJointMap(skeleton, Contract, idle));
            Assert.Equal(ContractJointMapRefusal.StanceNameMismatch, error.Reason);
            Assert.Equal("idle", error.ClipName);
            Assert.Equal("stance", error.ExpectedClipName);
            _ = new ContractJointMap(skeleton, Contract, idle, stanceClipName: "idle");

            AnimationClip stance = Clip("stance", Turn(Logical[HeadNode], Quaternion.Identity));
            var renamed = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(skeleton, Contract, stance, stanceClipName: "rest_pose"));
            Assert.Equal(ContractJointMapRefusal.StanceNameMismatch, renamed.Reason);
            Assert.Equal("stance", renamed.ClipName);
            Assert.Equal("rest_pose", renamed.ExpectedClipName);
            _ = new ContractJointMap(skeleton, Contract, stance);

            AnimationClip restPose = Clip("rest_pose", Turn(Logical[HeadNode], Quaternion.Identity));
            var map = new ContractJointMap(skeleton, Contract, restPose, stanceClipName: "rest_pose");
            Assert.Equal(AnimationSampler.SamplePose(restPose, skeleton, 0f), map.BaseLocal.ToArray());
        }

        [Theory]
        [InlineData(true, 2)]
        [InlineData(false, 2)]
        [InlineData(true, 0)]
        public void RefusesAStanceKeyingATrackTwice(bool rotation, int keyCount)
        {
            Skeleton skeleton = Build();
            int head = Logical[HeadNode];
            float[] times = keyCount == 0 ? [] : [0f, 1f];
            JointTrack track = rotation
                ? new JointTrack(head) { Rotation = new QuaternionTrack(times,
                    keyCount == 0 ? [] : [Quaternion.Identity, Quaternion.Identity], InterpolationMode.Linear) }
                : new JointTrack(head) { Translation = new Vector3Track(times,
                    [Vector3.Zero, Vector3.UnitY], InterpolationMode.Linear) };

            var error = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(skeleton, Contract, Clip("stance", track)));
            Assert.Equal(ContractJointMapRefusal.StanceTrackKeyCountMismatch, error.Reason);
            Assert.Equal("head", error.JointName);
            Assert.Equal("stance", error.ClipName);
            Assert.Equal(keyCount, error.KeyCount);
            Assert.Equal(1, error.ExpectedKeyCount);
            JointTrack single = rotation ? Turn(head, Quaternion.Identity) : Move(head, Vector3.Zero);
            _ = new ContractJointMap(skeleton, Contract, Clip("stance", single));
        }

        [Theory]
        [InlineData(LanternLogical, ContractJointMapRefusal.StanceNodeOutsideContract, "lantern")]
        [InlineData(99, ContractJointMapRefusal.StanceNodeMissing, null)]
        public void RefusesAStanceOnAJointOutsideTheContract(int logical, ContractJointMapRefusal reason,
            string? jointName)
        {
            Skeleton skeleton = Build();
            Assert.Equal(6, skeleton.NodeForLogicalIndex(LanternLogical));
            Assert.Equal(-1, skeleton.NodeForLogicalIndex(99));

            var error = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(skeleton, Contract, Clip("stance", Turn(logical, Quaternion.Identity))));
            Assert.Equal(reason, error.Reason);
            Assert.Equal(logical, error.TargetNode);
            Assert.Equal(jointName, error.JointName);
            Assert.Equal("stance", error.ClipName);
            _ = new ContractJointMap(skeleton, Contract, Clip("stance", Turn(Logical[HeadNode], Quaternion.Identity)));
        }

        [Fact]
        public void RefusesAStanceWithAScaleTrack()
        {
            var track = new JointTrack(Logical[SpineNode])
            {
                Scale = new Vector3Track(new[] { 0f }, new[] { new Vector3(1.1f) }, InterpolationMode.Step),
            };

            var error = Assert.Throws<ContractJointMapArgumentException>(
                () => new ContractJointMap(Build(), Contract, Clip("stance", track)));

            Assert.Equal(ContractJointMapRefusal.StanceScaleTrack, error.Reason);
            Assert.Equal("spine", error.JointName);
            Assert.Equal("stance", error.ClipName);
            _ = new ContractJointMap(Build(), Contract, Clip("stance", new JointTrack(Logical[SpineNode])));
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
            var error = Assert.Throws<ContractJointMapArgumentException>(() => map.SkinAtBase(other));
            Assert.Equal(ContractJointMapRefusal.MeshSkeletonMismatch, error.Reason);
            Assert.Equal("mesh", error.ParamName);
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
