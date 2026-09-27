using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    [Collection("AllocSensitive")]
    public class SkinnedGroundingTests
    {
        const float Epsilon = 1e-5f;

        const int RootNode = 0, HipsNode = 1, SpineNode = 2, LegNode = 3;

        // Skin bones in an order unlike the node order, so a bone read as a node shows.
        static readonly int[] Skin = { SpineNode, HipsNode, LegNode };

        static readonly Matrix4x4 Model = Matrix4x4.CreateScale(1.25f)
            * Matrix4x4.CreateFromYawPitchRoll(0.8f, 0.15f, -0.1f)
            * Matrix4x4.CreateTranslation(3f, -0.5f, 2f);

        [Fact]
        public void MinimumYEqualsTheLowestCpuSkinnedVertex()
        {
            (Matrix4x4[] inverseBind, Matrix4x4[] palette) = Posed();
            SkinnedVertex[] vertices = Vertices();

            var composed = new Matrix4x4[palette.Length];
            for (int bone = 0; bone < palette.Length; bone++)
                composed[bone] = SkinningMath.Compose(palette[bone], inverseBind[bone]);
            float expected = float.PositiveInfinity;
            foreach (SkinnedVertex vertex in vertices)
            {
                Vector3 skinned = SkinningMath.SkinVertex(vertex, composed).Position;
                expected = MathF.Min(expected, Vector3.Transform(skinned, Model).Y);
            }

            var scratch = new Vector4[palette.Length + 2];
            float actual = SkinnedGrounding.MinimumY(vertices, inverseBind, palette, Model, scratch);

            Assert.True(MathF.Abs(expected - actual) <= Epsilon, $"expected {expected}, got {actual}.");
            Assert.Equal(float.PositiveInfinity,
                SkinnedGrounding.MinimumY(ReadOnlySpan<SkinnedVertex>.Empty, inverseBind, palette, Model, scratch));
        }

        [Fact]
        public void MinimumYDrawsAnUnweightedVertexThroughTheModel()
        {
            (Matrix4x4[] inverseBind, _) = Posed();
            // Every bone lifts its vertices ten units, so only the unweighted vertex can be lowest.
            var palette = new Matrix4x4[inverseBind.Length];
            for (int bone = 0; bone < palette.Length; bone++)
            {
                Matrix4x4.Invert(inverseBind[bone], out Matrix4x4 bind);
                palette[bone] = bind * Matrix4x4.CreateTranslation(0f, 10f, 0f);
            }
            var unweighted = new Vector3(0.4f, 0.3f, -0.2f);
            SkinnedVertex[] vertices =
            {
                Vertex(new Vector3(0f, 0.5f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(unweighted, new Vector4(2f, 1f, 0f, 0f), Vector4.Zero),
                Vertex(new Vector3(0f, 1.5f, 0f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0.5f, 0.5f, 0f, 0f)),
            };

            float actual = SkinnedGrounding.MinimumY(vertices, inverseBind, palette, Model,
                new Vector4[palette.Length]);

            float expected = Vector3.Transform(unweighted, Model).Y;
            Assert.True(MathF.Abs(expected - actual) <= Epsilon, $"expected {expected}, got {actual}.");
        }

        [Fact]
        public void MinimumYRefusesAShortScratchOrUnpairedInverseBinds()
        {
            (Matrix4x4[] inverseBind, Matrix4x4[] palette) = Posed();
            SkinnedVertex[] vertices = Vertices();

            var shortScratch = Assert.Throws<ArgumentException>(() =>
                SkinnedGrounding.MinimumY(vertices, inverseBind, palette, Model, new Vector4[palette.Length - 1]));
            Assert.Equal("scratch", shortScratch.ParamName);
            var unpaired = Assert.Throws<ArgumentException>(() =>
                SkinnedGrounding.MinimumY(vertices, inverseBind.AsSpan(1), palette, Model,
                    new Vector4[palette.Length]));
            Assert.Equal("inverseBind", unpaired.ParamName);
        }

        [Fact]
        public void MinimumYAllocatesNothing()
        {
            (Matrix4x4[] inverseBind, Matrix4x4[] palette) = Posed();
            SkinnedVertex[] vertices = Vertices();
            var scratch = new Vector4[palette.Length];
            float sink = SkinnedGrounding.MinimumY(vertices, inverseBind, palette, Model, scratch);

            AllocAssert.NoPerCallAllocation("SkinnedGrounding.MinimumY", () =>
            {
                for (int call = 0; call < 100; call++)
                    sink += SkinnedGrounding.MinimumY(vertices, inverseBind, palette, Model, scratch);
            });
            Assert.True(float.IsFinite(sink));
        }

        // The rig's inverse binds, and a palette posed off its rest: the hips turn and drop, the spine bends
        // forward and the leg swings back.
        static (Matrix4x4[] InverseBind, Matrix4x4[] Palette) Posed()
        {
            Skeleton skeleton = Build();
            Matrix4x4[] rest = skeleton.ComposeRestPose();
            var inverseBind = new Matrix4x4[rest.Length];
            for (int bone = 0; bone < rest.Length; bone++)
                Assert.True(Matrix4x4.Invert(rest[bone], out inverseBind[bone]));

            var local = (JointPose[])skeleton.RestLocal.Clone();
            local[HipsNode].Translation += new Vector3(0.05f, -0.1f, 0f);
            local[HipsNode].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f);
            local[SpineNode].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
            local[LegNode].Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.9f);
            var palette = new Matrix4x4[skeleton.BoneCount];
            skeleton.ComposeInto(local, palette);
            return (inverseBind, palette);
        }

        static SkinnedVertex[] Vertices() =>
            new[]
            {
                // Bone 0 is the spine, 1 the hips, 2 the leg.
                Vertex(new Vector3(0.1f, 1.6f, 0.05f), new Vector4(0f, 1f, 0f, 0f), new Vector4(0.7f, 0.3f, 0f, 0f)),
                Vertex(new Vector3(-0.1f, 1.9f, -0.1f), new Vector4(0f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(new Vector3(0.2f, 1f, 0.1f), new Vector4(1f, 2f, 0f, 0f), new Vector4(0.6f, 0.4f, 0f, 0f)),
                Vertex(new Vector3(0.15f, 0.5f, 0.1f), new Vector4(2f, 1f, 0f, 0f), new Vector4(0.8f, 0.2f, 0f, 0f)),
                Vertex(new Vector3(0.15f, 0.05f, 0.2f), new Vector4(2f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f)),
                Vertex(new Vector3(0.2f, 0.02f, -0.15f), new Vector4(2f, 1f, 0f, 0f), new Vector4(0.9f, 0.1f, 0f, 0f)),
            };

        static SkinnedVertex Vertex(Vector3 position, Vector4 bones, Vector4 weights) =>
            new()
            {
                Position = position,
                Normal = Vector3.UnitY,
                Color = Vector4.One,
                BoneIndices = bones,
                BoneWeights = weights,
            };

        static Skeleton Build() =>
            new(
                new[] { -1, RootNode, HipsNode, HipsNode },
                new[]
                {
                    JointPose.Identity,
                    Pose(new Vector3(0f, 1f, 0f)),
                    Pose(new Vector3(0f, 0.4f, 0f)),
                    Pose(new Vector3(0.15f, -0.1f, 0f)),
                },
                new[] { 40, 41, 42, 43 },
                Skin,
                new[] { "root", "hips", "spine", "leg.L" });

        static JointPose Pose(Vector3 translation) =>
            new() { Translation = translation, Rotation = Quaternion.Identity, Scale = Vector3.One };
    }
}
