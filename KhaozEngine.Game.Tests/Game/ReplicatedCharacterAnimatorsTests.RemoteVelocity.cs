using System;
using System.Numerics;
using KhaozEngine.Game;
using Xunit;

namespace KhaozEngine.Tests.Game;

public partial class ReplicatedCharacterAnimatorsTests
{
    private const float RemoteFrameSeconds = 1f / 32f;
    private const float AuthoredWalkSpeed = 2f;

    [Fact]
    public void Remote_segment_speed_keeps_one_state_and_playback_rate_on_uneven_positions()
    {
        const float segmentSpeed = 4f;
        // The linear clip exposes time as a float translation. Subtracting two samples adds their roundoff.
        const float playbackRoundingTolerance = 0.000002f;
        var animators = CreateRemoteSpeedAnimators();
        Vector3 position = Vector3.Zero;
        animators.Update([new CharacterSample(1, position, grounded: true, verticalVelocity: 0f,
            planarSpeed: segmentSpeed)], RemoteFrameSeconds);
        CharacterPose first = Assert.Single(animators.Live);
        Assert.False(first.IsLocal);
        Assert.Equal(LocomotionState.Walk, first.State);
        float previous = first.Pose[0].Translation.X;

        float[] drawnSpeeds = [0f, 8f, 2f, 0f, 3f, 8f];
        Assert.NotEmpty(drawnSpeeds);
        foreach (float drawnSpeed in drawnSpeeds)
        {
            position.X += drawnSpeed * RemoteFrameSeconds;
            animators.Update([new CharacterSample(1, position, grounded: true, verticalVelocity: 0f,
                planarSpeed: segmentSpeed)], RemoteFrameSeconds);

            CharacterPose pose = Assert.Single(animators.Live);
            Assert.Equal(LocomotionState.Walk, pose.State);
            float now = pose.Pose[0].Translation.X;
            float expectedAdvance = RemoteFrameSeconds * (segmentSpeed / AuthoredWalkSpeed);
            Assert.InRange(MathF.Abs(now - previous - expectedAdvance), 0f, playbackRoundingTolerance);
            previous = now;
        }
    }

    [Fact]
    public void Remote_without_segment_speed_still_uses_the_position_delta()
    {
        var animators = CreateRemoteSpeedAnimators();
        (float DrawnSpeed, LocomotionState State)[] frames =
        [
            (0f, LocomotionState.Idle),
            (2f, LocomotionState.Walk),
            (8f, LocomotionState.Run),
            (0f, LocomotionState.Idle),
        ];

        Vector3 position = Vector3.Zero;
        Assert.NotEmpty(frames);
        foreach (var (speed, state) in frames)
        {
            position.X += speed * RemoteFrameSeconds;
            animators.Update([new CharacterSample(1, position, isLocal: false, grounded: true, verticalVelocity: 0f)],
                RemoteFrameSeconds);

            Assert.Equal(state, Assert.Single(animators.Live).State);
        }
    }

    private static ReplicatedCharacterAnimators CreateRemoteSpeedAnimators()
    {
        CharacterAnimatorTuning tuning = CharacterAnimatorTuning.Default;
        tuning.Locomotion = new LocomotionThresholds(walkSpeed: 0.1f, runSpeed: 6f);
        tuning.Crossfade = 0f;
        tuning.StateDebounceSeconds = 0f;
        tuning.VelocityWindowSeconds = 0f;
        tuning.SyncLocomotionToSpeed = true;
        tuning.WalkClipSpeed = AuthoredWalkSpeed;
        tuning.RunClipSpeed = 6f;
        return new ReplicatedCharacterAnimators(OneBone(), RampClips(), tuning);
    }
}
