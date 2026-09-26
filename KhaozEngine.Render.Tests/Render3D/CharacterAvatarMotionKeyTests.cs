using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// <see cref="CharacterAvatar"/> owns a motion key for its lifetime (TEMPORAL-FOUNDATIONS-DESIGN section 3): two
    /// avatars differ, one avatar keeps its key across frames, and the key reaches the queue and the motion history.
    /// </summary>
    // CharacterAvatar is Obsolete and exercised on purpose, as in CharacterAvatarTests.
#pragma warning disable CS0618
    public sealed class CharacterAvatarMotionKeyTests
    {
        // A one-bone rig with a skeleton, as CharacterAvatarHandleOwnershipTests builds it.
        static SkinnedGltfMesh RiggedTube(out Skeleton skeleton)
        {
            SkinnedGltfMesh tube = SkinnedMeshBuilder.BuildTube(0.5f, 2f, 4, 6, 1, Axis.Z);
            skeleton = new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });
            return new SkinnedGltfMesh(tube.Vertices, tube.Indices32, tube.InverseBind, tube.RestPose, skeleton);
        }

        static CharacterAvatar NewAvatar(Skeleton skeleton, SkinnedMeshHandle handle)
        {
            var track = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { Vector3.Zero, Vector3.Zero },
                    InterpolationMode.Linear),
            };
            var clips = new Dictionary<LocomotionState, AnimationClip>
            {
                [LocomotionState.Idle] = new AnimationClip("Idle", 1f, new List<JointTrack> { track }),
            };
            var animation = new AnimatedCharacter(skeleton, clips, new LocomotionThresholds(0.1f, 9f));
            return new CharacterAvatar(new CharacterController3D { CapsuleHalfHeight = 0.9f }, animation, handle);
        }

        [Fact]
        public void Each_avatar_draws_under_its_own_key_for_its_whole_life()
        {
            using var harness = new MotionTestScene();
            Scene3D scene = harness.Scene;
            SkinnedMeshHandle handle = scene.LoadSkinnedMesh(RiggedTube(out Skeleton skeleton));
            CharacterAvatar first = NewAvatar(skeleton, handle), second = NewAvatar(skeleton, handle);
            MotionKey firstKey = first.Motion;

            Assert.False(firstKey.IsNone);
            Assert.False(second.Motion.IsNone);
            Assert.NotEqual(firstKey, second.Motion);

            for (int frame = 0; frame < 3; frame++)
            {
                scene.Begin();
                first.Draw(scene);
                second.Draw(scene);
                Assert.Equal(firstKey, scene.QueuedSkinnedInstancesForTests[0].Motion);
                Assert.Equal(second.Motion, scene.QueuedSkinnedInstancesForTests[1].Motion);
            }
        }

        [Fact]
        public void With_temporal_active_the_avatars_last_pose_is_remembered()
        {
            using var harness = new MotionTestScene();
            Scene3D scene = harness.Scene;
            scene.ForceTemporalForTests = true;
            SkinnedGltfMesh mesh = RiggedTube(out Skeleton skeleton);
            CharacterAvatar avatar = NewAvatar(skeleton, scene.LoadSkinnedMesh(mesh));

            scene.Begin();
            avatar.Draw(scene);
            Matrix4x4 drawnModel = Assert.Single(scene.QueuedSkinnedInstancesForTests).World;
            scene.Begin();
            avatar.Draw(scene);

            MotionHistory? history = scene.ActiveMotionHistory;
            Assert.NotNull(history);
            Assert.True(history.TryGetPreviousSkinned(avatar.Motion, out Matrix4x4 previousModel,
                out ReadOnlySpan<Matrix4x4> palette));
            Assert.Equal(drawnModel, previousModel);
            Assert.Equal(mesh.InverseBind.Length, palette.Length);
        }
    }
#pragma warning restore CS0618
}
