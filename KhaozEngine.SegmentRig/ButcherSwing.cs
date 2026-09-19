using System;

namespace KhaozEngine.SegmentRig;

/// <summary>A two-handed cutting loop sampled from elapsed seconds.</summary>
public static class ButcherSwing
{
    /// <summary>Samples one cutting loop.</summary>
    /// <param name="elapsedSeconds">Seconds elapsed since the action began.</param>
    /// <param name="cycleDurationSeconds">Seconds per cutting cycle. Must be positive and finite.</param>
    public static WalkPose PoseAt(float elapsedSeconds, float cycleDurationSeconds)
    {
        float phase = PhaseAt(elapsedSeconds, cycleDurationSeconds);
        float cut = 0.5f - (0.5f * MathF.Cos(phase * 2f * MathF.PI));
        return new WalkPose(
            LeftArm: Lerp(1.1f, 0.72f, cut),
            RightArm: Lerp(1.35f, 0.82f, cut),
            LeftLeg: 0f,
            RightLeg: 0f,
            LeftElbow: Lerp(1.25f, 0.95f, cut),
            RightElbow: Lerp(0.85f, 0.62f, cut),
            RightArmYaw: Lerp(0.3f, -0.24f, cut),
            RightWrist: Lerp(0.4f, 0.88f, cut),
            TorsoLean: Lerp(0.1f, 0.2f, cut),
            LeftArmYaw: Lerp(-0.1f, -0.28f, cut),
            LeftWrist: Lerp(0.15f, -0.12f, cut));
    }

    /// <summary>Lays the cutting loop over a body's current pose.</summary>
    public static WalkPose Compose(in WalkPose pose, float elapsedSeconds, float cycleDurationSeconds,
        float weight)
    {
        float w = Math.Clamp(weight, 0f, 1f);
        if (w <= 0f) return pose;
        WalkPose swing = PoseAt(elapsedSeconds, cycleDurationSeconds);
        return pose with
        {
            LeftArm = Lerp(pose.LeftArm, swing.LeftArm, w),
            RightArm = Lerp(pose.RightArm, swing.RightArm, w),
            LeftElbow = Lerp(pose.LeftElbow, swing.LeftElbow, w),
            RightElbow = Lerp(pose.RightElbow, swing.RightElbow, w),
            RightArmYaw = Lerp(pose.RightArmYaw, swing.RightArmYaw, w),
            RightWrist = Lerp(pose.RightWrist, swing.RightWrist, w),
            TorsoLean = Lerp(pose.TorsoLean, swing.TorsoLean, w),
            LeftArmYaw = Lerp(pose.LeftArmYaw, swing.LeftArmYaw, w),
            LeftWrist = Lerp(pose.LeftWrist, swing.LeftWrist, w),
        };
    }

    static float PhaseAt(float elapsedSeconds, float cycleDurationSeconds)
    {
        if (!float.IsFinite(cycleDurationSeconds) || cycleDurationSeconds <= 0f)
            throw new ArgumentOutOfRangeException(nameof(cycleDurationSeconds), cycleDurationSeconds,
                "A cutting cycle duration must be positive and finite.");
        if (!float.IsFinite(elapsedSeconds)) return 0f;
        float phase = elapsedSeconds / cycleDurationSeconds;
        return phase - MathF.Floor(phase);
    }

    static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}
