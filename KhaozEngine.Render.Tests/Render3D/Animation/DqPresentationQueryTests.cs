using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public sealed class DqPresentationQueryTests
    {
        static readonly SkinningValidationContext Context = new("synthetic-presentation-query", "stance", 0);
        static readonly Vector3 Pivot = new(2f, 1f, 0f);
        static readonly Matrix4x4 Model = Matrix4x4.CreateScale(2f, 3f, 0.5f)
            * new Matrix4x4(0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1)
            * Matrix4x4.CreateTranslation(7f, -4f, 2f);
        static readonly float L = MathF.Sqrt(38f);

        [Fact]
        public void DQ_grounding_equals_independently_posed_world_minimum()
        {
            Skeleton skeleton = Rig();
            SkinnedGltfMesh mesh = Mesh(skeleton, SkinningProfile.DualQuaternionRigid);
            var joints = new Matrix4x4[skeleton.BoneCount];
            skeleton.ComposeInto(AnimationSampler.SamplePose(Stance(), skeleton, 0f), joints);
            var prepared = new RigidDualQuaternion[joints.Length];
            DualQuaternionPalette.Prepare(joints, mesh.InverseBind, prepared, Context);

            float independentlyPosedMinimum = float.PositiveInfinity;
            Vector3[] expected = DqPositions();
            for (int i = 0; i < expected.Length; i++)
            {
                float worldY = Vector3.Transform(expected[i], Model).Y;
                independentlyPosedMinimum = MathF.Min(independentlyPosedMinimum, worldY);
                float vertexMinimum = SkinnedGrounding.MinimumY([mesh.Vertices[i]], mesh.Profile, [], prepared, Model);
                Assert.InRange(MathF.Abs(vertexMinimum - worldY), 0f, 2e-5f * L);
            }

            float actualMinimum = SkinnedGrounding.MinimumY(mesh.Vertices, mesh.Profile, [], prepared, Model);
            float emptyMinimum = SkinnedGrounding.MinimumY([], mesh.Profile, [], prepared, Model);
            Assert.InRange(MathF.Abs(actualMinimum - independentlyPosedMinimum), 0f, 2e-5f * L);
            Assert.Equal(float.PositiveInfinity, emptyMinimum);

            float lbsMinimum = SkinnedGrounding.MinimumY(mesh.Vertices, mesh.InverseBind, joints, Model,
                new Vector4[joints.Length]);
            Assert.True(MathF.Abs(lbsMinimum - independentlyPosedMinimum) > 0.4f);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Base_stance_measurement_uses_the_skin_profile(bool dualQuaternion)
        {
            Skeleton skeleton = Rig();
            SkinningProfile profile = dualQuaternion ? SkinningProfile.DualQuaternionRigid : SkinningProfile.LinearBlend;
            var map = new ContractJointMap(skeleton, Contract(), Stance());

            Vector3[] actual = map.SkinAtBase(Mesh(skeleton, profile));
            Vector3[] expected = dualQuaternion ? DqPositions() :
                [new(1.5f, -0.5f, 0f), new(2.5f, 1.5f, 1f), new(3f, 1f, -1f),
                 new(4f, 2f, 0f), new(5f, 3f, -2f)];
            Assert.Equal(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.InRange(MathF.Abs(actual[i].X - expected[i].X), 0f, 2e-5f * L);
                Assert.InRange(MathF.Abs(actual[i].Y - expected[i].Y), 0f, 2e-5f * L);
                Assert.InRange(MathF.Abs(actual[i].Z - expected[i].Z), 0f, 2e-5f * L);
            }

            var error = Assert.Throws<ArgumentException>(() => map.SkinAtBase(Mesh(Rig(), profile)));
            Assert.Equal("mesh", error.ParamName);
            Assert.Contains("same skeleton", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Prepared_LBS_grounding_preserves_height_columns_and_joint_scale()
        {
            SkinnedGltfMesh mesh = Mesh(Rig(), SkinningProfile.LinearBlend);
            Matrix4x4[] joints =
            [Matrix4x4.CreateScale(2f, 3f, 4f) * Matrix4x4.CreateTranslation(1f, -2f, 3f),
             Matrix4x4.CreateScale(0.5f, 1.5f, 2f) * Matrix4x4.CreateTranslation(-1f, 4f, 2f)];
            var composed = new Matrix4x4[joints.Length];
            for (int bone = 0; bone < joints.Length; bone++)
                composed[bone] = SkinningMath.Compose(joints[bone], mesh.InverseBind[bone]);

            float legacy = SkinnedGrounding.MinimumY(mesh.Vertices, mesh.InverseBind, joints, Model,
                new Vector4[joints.Length]);
            Assert.Equal(legacy, SkinnedGrounding.MinimumY(mesh.Vertices, mesh.Profile, composed, [], Model));
            Assert.Equal(float.PositiveInfinity, SkinnedGrounding.MinimumY([], mesh.Profile, composed, [], Model));
        }

        // A common pivot means an equal DQ blend of identity and Z90 is Z45 about that pivot.
        // These positions are analytic fixture expectations, independent of the engine's DQ evaluator.
        static Vector3[] DqPositions()
        {
            float s = MathF.Sqrt(0.5f);
            return [new(2f - s, 1f - 3f * s, 0f), new(2f + s, 1f + s, 1f),
                    new(3f, 1f, -1f), new(4f, 2f, 0f), new(5f, 3f, -2f)];
        }

        static Skeleton Rig() => new(
            [-1, 0],
            [JointPose.Identity, new JointPose { Translation = Pivot, Rotation = Quaternion.Identity, Scale = Vector3.One }],
            [10, 20], [0, 1], ["root", "bend"]);

        static SkeletonContract Contract() => new(
            [new ContractJoint("root", null, true), new ContractJoint("bend", "root", true)]);

        static AnimationClip Stance() => new("stance", 0f,
            [new JointTrack(20)
            {
                Rotation = new QuaternionTrack([0f], [new Quaternion(0f, 0f, MathF.Sqrt(0.5f), MathF.Sqrt(0.5f))],
                    InterpolationMode.Step),
            }]);

        static SkinnedGltfMesh Mesh(Skeleton skeleton, SkinningProfile profile) => new(
            [Vertex(new Vector3(0f, 0f, 0f), new Vector4(0.5f, 0.5f, 0f, 0f)),
             Vertex(new Vector3(3f, 1f, 1f), new Vector4(0.5f, 0.5f, 0f, 0f)),
             Vertex(new Vector3(2f, 0f, -1f), Vector4.UnitY),
             Vertex(new Vector3(4f, 2f, 0f), Vector4.UnitX),
             Vertex(new Vector3(5f, 3f, -2f), Vector4.Zero)],
            new ushort[] { 0, 1, 2, 2, 3, 4 },
            [Matrix4x4.Identity, Matrix4x4.CreateTranslation(-Pivot)], skeleton.ComposeRestPose(), skeleton, profile);

        static SkinnedVertex Vertex(Vector3 position, Vector4 weights) => new()
        {
            Position = position, Normal = Vector3.UnitY, Color = Vector4.One,
            BoneIndices = new Vector4(0f, 1f, 0f, 0f), BoneWeights = weights,
        };

    }
}
