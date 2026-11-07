using System;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class SkeletonAdmissionTests
    {
        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 0)]
        public void NodeArraysMustCoverTheSameNodes(int restCount, int logicalCount)
        {
            var error = Assert.Throws<SkeletonArgumentException>(() => new Skeleton(
                new[] { -1 }, new JointPose[restCount], new int[logicalCount], new[] { 0 }));
            Assert.Equal(SkeletonRefusal.NodeArrayLengthsMismatch, error.Reason);
            Assert.Equal(1, error.ParentCount);
            Assert.Equal(restCount, error.RestCount);
            Assert.Equal(logicalCount, error.LogicalIndexCount);
            Assert.Null(error.ParamName);
            Assert.Equal(1, OneBone().NodeCount);
        }

        [Fact]
        public void NamesMustCoverEveryNode()
        {
            var error = Assert.Throws<SkeletonArgumentException>(() => new Skeleton(
                new[] { -1 }, new[] { JointPose.Identity }, new[] { 10 }, new[] { 0 }, Array.Empty<string>()));
            Assert.Equal(SkeletonRefusal.NameCountMismatch, error.Reason);
            Assert.Equal(0, error.NameCount);
            Assert.Equal(1, error.NodeCount);
            Assert.Equal("nodeNames", error.ParamName);
            Assert.Equal(0, OneBone().IndexOf("root"));
        }

        [Theory]
        [InlineData(0, 1, SkeletonRefusal.LocalPoseCountMismatch, 2)]
        [InlineData(2, 0, SkeletonRefusal.BonePaletteCountMismatch, 1)]
        public void ComposeBuffersMustCoverTheirNodesAndBones(int poseCount, int paletteCount,
            SkeletonRefusal reason, int expectedCount)
        {
            // A non-deforming root makes node and bone counts distinct.
            var skeleton = new Skeleton(new[] { -1, 0 }, new[] { JointPose.Identity, JointPose.Identity },
                new[] { 10, 11 }, new[] { 1 });
            var error = Assert.Throws<SkeletonArgumentException>(
                () => skeleton.ComposeInto(new JointPose[poseCount], new Matrix4x4[paletteCount]));
            Assert.Equal(reason, error.Reason);
            Assert.Equal(0, error.ActualCount);
            Assert.Equal(expectedCount, error.ExpectedCount);
            Assert.Null(error.ParamName);
            var palette = new Matrix4x4[1];
            skeleton.ComposeInto(new[] { JointPose.Identity, JointPose.Identity }, palette);
            Assert.Equal(Matrix4x4.Identity, palette[0]);
        }

        static Skeleton OneBone() => new(new[] { -1 }, new[] { JointPose.Identity }, new[] { 10 }, new[] { 0 },
            new[] { "root" });
    }
}
