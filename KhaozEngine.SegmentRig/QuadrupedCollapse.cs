using System;

namespace KhaozEngine.SegmentRig;

/// <summary>Samples a smooth fall from the authored standing pose to a caller-authored quadruped resting pose.</summary>
public static class QuadrupedCollapse
{
    /// <summary>Samples the collapse by normalized progress.</summary>
    /// <param name="progress">Collapse progress. Values outside zero through one are clamped.</param>
    /// <param name="restingPose">The caller-authored final pose.</param>
    public static QuadrupedPose PoseAt(float progress, in QuadrupedPose restingPose)
    {
        if (!float.IsFinite(progress) || progress <= 0f) return QuadrupedPose.Rest;
        if (progress >= 1f) return restingPose;
        float t = progress * progress * (3f - (2f * progress));
        return restingPose with
        {
            LeftForeSwing = restingPose.LeftForeSwing * t,
            RightForeSwing = restingPose.RightForeSwing * t,
            LeftHindSwing = restingPose.LeftHindSwing * t,
            RightHindSwing = restingPose.RightHindSwing * t,
            LeftForeFlex = restingPose.LeftForeFlex * t,
            RightForeFlex = restingPose.RightForeFlex * t,
            LeftHindFlex = restingPose.LeftHindFlex * t,
            RightHindFlex = restingPose.RightHindFlex * t,
            Bob = restingPose.Bob * t,
            HeadNod = restingPose.HeadNod * t,
            Roll = restingPose.Roll * t,
            TrunkYaw = restingPose.TrunkYaw * t,
            HeadYaw = restingPose.HeadYaw * t,
            Surge = restingPose.Surge * t,
            Pitch = restingPose.Pitch * t,
            RootOffset = restingPose.RootOffset * t,
            RootRoll = restingPose.RootRoll * t,
            LeftForeSplay = restingPose.LeftForeSplay * t,
            RightForeSplay = restingPose.RightForeSplay * t,
            LeftHindSplay = restingPose.LeftHindSplay * t,
            RightHindSplay = restingPose.RightHindSplay * t,
        };
    }

    /// <summary>Samples the collapse by elapsed seconds and duration.</summary>
    /// <param name="elapsedSeconds">Seconds elapsed since the collapse began.</param>
    /// <param name="durationSeconds">Seconds until the resting pose is reached. Must be positive and finite.</param>
    /// <param name="restingPose">The caller-authored final pose.</param>
    public static QuadrupedPose PoseAt(float elapsedSeconds, float durationSeconds,
        in QuadrupedPose restingPose)
    {
        if (!float.IsFinite(durationSeconds) || durationSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds,
                "A collapse duration must be positive and finite.");
        return PoseAt(elapsedSeconds / durationSeconds, restingPose);
    }
}
