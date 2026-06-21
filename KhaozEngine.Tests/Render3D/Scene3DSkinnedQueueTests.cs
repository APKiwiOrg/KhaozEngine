using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class Scene3DSkinnedQueueTests
    {
        [Fact]
        public void ComposeBones_RestPose_FillsIdentityBlock()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 2, 4, 3);
            var dst = new System.Collections.Generic.List<Matrix4x4>();
            uint offset = Scene3D.ComposeBonesInto(dst, tube.RestPose, tube.InverseBind);
            Assert.Equal(0u, offset);
            Assert.Equal(3, dst.Count);
            foreach (var m in dst)
                Assert.True(Vector3.Distance(Vector3.Transform(new Vector3(1, 2, 3), m), new Vector3(1, 2, 3)) < 1e-3f);
        }

        [Fact]
        public void ComposeBones_SecondCall_AppendsAtRunningOffset()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 2, 4, 3);
            var dst = new System.Collections.Generic.List<Matrix4x4>();
            Scene3D.ComposeBonesInto(dst, tube.RestPose, tube.InverseBind);
            uint offset2 = Scene3D.ComposeBonesInto(dst, tube.RestPose, tube.InverseBind);
            Assert.Equal(3u, offset2);
            Assert.Equal(6, dst.Count);
        }

        [Fact]
        public void ComposeBones_WrongBoneCount_Throws()
        {
            var dst = new System.Collections.Generic.List<Matrix4x4>();
            Assert.Throws<ArgumentException>(() =>
                Scene3D.ComposeBonesInto(dst, new[] { Matrix4x4.Identity }, new[] { Matrix4x4.Identity, Matrix4x4.Identity }));
        }
    }
}
