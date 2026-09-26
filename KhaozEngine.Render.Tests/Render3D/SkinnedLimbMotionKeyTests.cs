using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// <see cref="SkinnedLimb"/> owns a motion key for its lifetime (TEMPORAL-FOUNDATIONS-DESIGN section 3): two limbs
    /// differ, one limb keeps its key across frames and both draw overloads, and the key reaches the motion history.
    /// </summary>
    public sealed class SkinnedLimbMotionKeyTests
    {
        static SkinnedLimb NewLimb(Scene3D scene) =>
            new(scene, radius: 0.4f, length: 2.5f, ringSegments: 8, radialSegments: 8, boneCount: 5,
                ChainConfig.Writhe, Axis.Z);

        [Fact]
        public void Each_limb_draws_under_its_own_key_through_both_overloads()
        {
            using var harness = new MotionTestScene();
            Scene3D scene = harness.Scene;
            using SkinnedLimb first = NewLimb(scene), second = NewLimb(scene);
            MotionKey firstKey = first.Motion;

            Assert.False(firstKey.IsNone);
            Assert.False(second.Motion.IsNone);
            Assert.NotEqual(firstKey, second.Motion);

            for (int frame = 0; frame < 3; frame++)
            {
                scene.Begin();
                first.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, frame / 60f);
                second.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, frame / 60f);
                first.Draw(scene, Matrix4x4.Identity, Color.White);
                second.Draw(scene, Matrix4x4.Identity, Color.White, Material.None);
                Assert.Equal(firstKey, scene.QueuedSkinnedInstancesForTests[0].Motion);
                Assert.Equal(second.Motion, scene.QueuedSkinnedInstancesForTests[1].Motion);
            }
        }

        [Fact]
        public void With_temporal_active_the_limbs_last_pose_is_remembered()
        {
            using var harness = new MotionTestScene();
            Scene3D scene = harness.Scene;
            scene.ForceTemporalForTests = true;
            using SkinnedLimb limb = NewLimb(scene);

            // The model moves between the frames, so the recorded model can only match the first frame's.
            scene.Begin();
            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 0f);
            limb.Draw(scene, Matrix4x4.CreateTranslation(1f, 0f, 0f), Color.White);
            Matrix4x4 drawnModel = Assert.Single(scene.QueuedSkinnedInstancesForTests).World;
            scene.Begin();
            limb.Update(Vector3.Zero, Vector3.UnitZ, Vector3.UnitY, 1f / 60f);
            limb.Draw(scene, Matrix4x4.CreateTranslation(1.5f, 0f, 0f), Color.White);

            MotionHistory? history = scene.ActiveMotionHistory;
            Assert.NotNull(history);
            Assert.True(history.TryGetPreviousSkinned(limb.Motion, out Matrix4x4 previousModel,
                out ReadOnlySpan<Matrix4x4> palette));
            Assert.Equal(drawnModel, previousModel);
            Assert.Equal(limb.BoneCount, palette.Length);
            Assert.Equal(5, palette.Length);
        }
    }
}
