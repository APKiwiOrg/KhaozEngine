using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class SkeletonComposeTests
    {
        // A 3-node vertical chain: node0 root at origin, node1 +1 Y under node0, node2 +1 Y under node1.
        // All three nodes are bones (JointToNode = identity), logical node indices offset by 10 to prove the lookup.
        static Skeleton Chain3()
        {
            var parents = new[] { -1, 0, 1 };
            var rest = new[]
            {
                new JointPose { Translation = new Vector3(0, 0, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
                new JointPose { Translation = new Vector3(0, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
                new JointPose { Translation = new Vector3(0, 1, 0), Rotation = Quaternion.Identity, Scale = Vector3.One },
            };
            var nodeLogical = new[] { 10, 11, 12 };
            var jointToNode = new[] { 0, 1, 2 };
            return new Skeleton(parents, rest, nodeLogical, jointToNode);
        }

        [Fact]
        public void ComposeRestPose_AccumulatesUpTheChain()
        {
            Skeleton s = Chain3();
            Matrix4x4[] palette = s.ComposeRestPose();
            Assert.Equal(3, palette.Length);
            Assert.True(Vector3.Distance(palette[0].Translation, new Vector3(0, 0, 0)) < 1e-5f);
            Assert.True(Vector3.Distance(palette[1].Translation, new Vector3(0, 1, 0)) < 1e-5f);
            Assert.True(Vector3.Distance(palette[2].Translation, new Vector3(0, 2, 0)) < 1e-5f);
        }

        [Fact]
        public void NodeForLogicalIndex_RoundTrips()
        {
            Skeleton s = Chain3();
            Assert.Equal(0, s.NodeForLogicalIndex(10));
            Assert.Equal(1, s.NodeForLogicalIndex(11));
            Assert.Equal(2, s.NodeForLogicalIndex(12));
            Assert.Equal(-1, s.NodeForLogicalIndex(999));
        }

        [Fact]
        public void BoneCount_And_NodeCount()
        {
            Skeleton s = Chain3();
            Assert.Equal(3, s.NodeCount);
            Assert.Equal(3, s.BoneCount);
        }
    }
}
