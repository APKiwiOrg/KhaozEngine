using System;

namespace KhaozEngine.SegmentRig;

/// <summary>The restrained two-handed pose used for processing actions.</summary>
/// <remarks>One stroke serves every processing action. Station work leans into the station with nothing held in
/// either hand. The nonstation form keeps the torso upright as a safe fallback.</remarks>
public static class ProcessingSwing
{
    /// <summary>Seconds the visible processing stroke takes to settle back into its working hold.</summary>
    public const float StrokeSeconds = 0.55f;

    /// <summary>Seconds the action takes to enter or leave its working pose.</summary>
    public const float BlendSeconds = 0.15f;

    /// <summary>Lays one processing stroke over a body's current pose.</summary>
    /// <param name="pose">The pose underneath the action.</param>
    /// <param name="phase">Stroke progress from zero at the cadence edge to one at the working hold.</param>
    /// <param name="atStation">Whether the action has a station target. Station work leans into the bench
    /// and lowers the off hand toward it.</param>
    /// <param name="weight">How much of the action is visible.</param>
    public static WalkPose Compose(in WalkPose pose, float phase, bool atStation, float weight)
    {
        float w = Math.Clamp(weight, 0f, 1f);
        if (w <= 0f) return pose;
        float t = Smooth(Math.Clamp(phase, 0f, 1f));

        float rightArm = Lerp(0.55f, 0.25f, t);
        float rightElbow = Lerp(0.95f, 1.15f, t);
        float rightYaw = Lerp(-0.35f, -0.12f, t);
        float rightWrist = Lerp(0.75f, 0.28f, t);
        float leftArm = atStation ? Lerp(0.7f, 0.45f, t) : 0.55f;
        float leftElbow = atStation ? Lerp(1.05f, 1.3f, t) : 1.45f;
        float leftYaw = atStation ? 0.18f : 0.4f;
        float lean = atStation ? Lerp(0.14f, 0.09f, t) : 0f;

        return pose with
        {
            RightArm = Lerp(pose.RightArm, rightArm, w),
            RightElbow = Lerp(pose.RightElbow, rightElbow, w),
            RightArmYaw = Lerp(pose.RightArmYaw, rightYaw, w),
            RightWrist = Lerp(pose.RightWrist, rightWrist, w),
            LeftArm = Lerp(pose.LeftArm, leftArm, w),
            LeftElbow = Lerp(pose.LeftElbow, leftElbow, w),
            LeftArmYaw = Lerp(pose.LeftArmYaw, leftYaw, w),
            TorsoLean = Lerp(pose.TorsoLean, lean, w),
        };
    }

    static float Smooth(float value) => value * value * (3f - (2f * value));

    static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
}
