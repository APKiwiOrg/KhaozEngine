using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class Scene3DSkinnedQueueTests
    {
        const int Cap = SkinnedModelRenderer.MaxBonesPerDraw;

        [Fact]
        public void ComposeBonesIntoSlot_RestPose_FillsIdentityWindow()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 2, 4, 3);
            var dst = new List<Matrix4x4>();
            Scene3D.ComposeBonesIntoSlot(dst, 0, tube.RestPose, tube.InverseBind);
            // Slot 0 occupies the whole per-draw window (padded to MaxBonesPerDraw).
            Assert.Equal(Cap, dst.Count);
            // Rest pose composes to identity, and the pad entries are identity too: nothing in the window deforms.
            foreach (var m in dst)
                Assert.True(Vector3.Distance(Vector3.Transform(new Vector3(1, 2, 3), m), new Vector3(1, 2, 3)) < 1e-3f);
        }

        [Fact]
        public void ComposeBonesIntoSlot_SecondSlot_StartsAtSlotStride()
        {
            var tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 2, 4, 3);
            var dst = new List<Matrix4x4>();
            Scene3D.ComposeBonesIntoSlot(dst, 0, tube.RestPose, tube.InverseBind);
            Scene3D.ComposeBonesIntoSlot(dst, 1, tube.RestPose, tube.InverseBind);
            // Two slots -> the list spans 2 windows; slot 1's bones begin at matrix index Cap.
            Assert.Equal(2 * Cap, dst.Count);
        }

        [Fact]
        public void ComposeBonesIntoSlot_WrongBoneCount_Throws()
        {
            var dst = new List<Matrix4x4>();
            Assert.Throws<ArgumentException>(() =>
                Scene3D.ComposeBonesIntoSlot(dst, 0, new[] { Matrix4x4.Identity }, new[] { Matrix4x4.Identity, Matrix4x4.Identity }));
        }
    }
}
