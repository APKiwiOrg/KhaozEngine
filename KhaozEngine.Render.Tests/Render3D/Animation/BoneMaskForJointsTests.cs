using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class BoneMaskForJointsTests
    {
        // The spine carries the head, so a subtree mask would take it and a joint mask must not.
        static readonly int[] Parents = { -1, 0, 1, 2, 1, 0 };
        static readonly string[] Names = { "root", "hips", "spine", "head", "leg.L", "" };

        [Fact]
        public void ForJointsWeighsOnlyTheNamedJoints()
        {
            Skeleton skeleton = Build();

            BoneMask mask = BoneMask.ForJoints(skeleton, new[] { "spine", "leg.L", "spine" });
            Assert.Equal(skeleton.NodeCount, mask.NodeCount);
            Assert.Equal(new[] { 0f, 0f, 1f, 0f, 1f, 0f }, Weights(mask));

            Assert.Equal(new[] { 0f, 0.25f, 0f, 0f, 0f, 0f }, Weights(BoneMask.ForJoints(skeleton, new[] { "hips" }, 0.25f)));
            Assert.Equal(new[] { 1f, 0f, 0f, 0f, 0f, 0f }, Weights(BoneMask.ForJoints(skeleton, new[] { "root" }, 3f)));
            Assert.Equal(new float[6], Weights(BoneMask.ForJoints(skeleton, Array.Empty<string>())));
        }

        [Fact]
        public void ForJointsRefusesAMissingName()
        {
            Skeleton skeleton = Build();

            var missing = Assert.Throws<BoneMaskArgumentException>(
                () => BoneMask.ForJoints(skeleton, new[] { "spine", "tail" }));
            Assert.Equal("jointNames", missing.ParamName);
            Assert.Equal(BoneMaskRefusal.JointNameMissing, missing.Reason);
            Assert.Equal("tail", missing.JointName);
            Assert.IsAssignableFrom<ArgumentException>(missing);
            Assert.Equal(1f, BoneMask.ForJoints(skeleton, new[] { "spine" }).Weight(2));

            // An unnamed node has no name to be asked for.
            var unnamed = Assert.Throws<BoneMaskArgumentException>(() => BoneMask.ForJoints(skeleton, new[] { "" }));
            Assert.Equal("jointNames", unnamed.ParamName);
            Assert.Equal(BoneMaskRefusal.JointNameMissing, unnamed.Reason);
            Assert.Equal("", unnamed.JointName);
            var nullName = Assert.Throws<BoneMaskArgumentException>(() => BoneMask.ForJoints(skeleton, new string[] { null! }));
            Assert.Equal(BoneMaskRefusal.JointNameMissing, nullName.Reason);
            Assert.Null(nullName.JointName);
            Assert.Equal("jointNames", nullName.ParamName);

            Assert.Throws<ArgumentNullException>(() => BoneMask.ForJoints(null!, new[] { "spine" }));
            Assert.Throws<ArgumentNullException>(() => BoneMask.ForJoints(skeleton, null!));
        }

        [Fact]
        public void SubtreeRefusesAnExplicitNameTableWithTheWrongCount()
        {
            Skeleton skeleton = Build();
            var error = Assert.Throws<BoneMaskArgumentException>(
                () => BoneMask.Subtree(skeleton, "spine", new[] { "root", "hips", "spine" }, 1f));
            Assert.Equal(BoneMaskRefusal.NameCountMismatch, error.Reason);
            Assert.Equal(3, error.NameCount);
            Assert.Equal(6, error.NodeCount);
            Assert.Equal("boneNames", error.ParamName);
            Assert.Equal(1f, BoneMask.Subtree(skeleton, "spine", Names, 1f).Weight(2));
        }

        static float[] Weights(BoneMask mask)
        {
            var weights = new float[mask.NodeCount];
            for (int node = 0; node < weights.Length; node++) weights[node] = mask.Weight(node);
            return weights;
        }

        static Skeleton Build()
        {
            var rest = new JointPose[Parents.Length];
            for (int node = 0; node < rest.Length; node++)
                rest[node] = new JointPose
                {
                    Translation = new Vector3(0f, 0.2f * node, 0f),
                    Rotation = Quaternion.Identity,
                    Scale = Vector3.One,
                };
            return new Skeleton(Parents, rest, new[] { 30, 31, 32, 33, 34, 35 }, new[] { 1, 2, 3, 4 }, Names);
        }
    }
}
