namespace KhaozEngine.Game
{
    /// <summary>One locomotion clip in a <see cref="DirectionalGaitSet"/> family. <paramref name="ClipId"/> is the
    /// consumer's own clip key. <paramref name="FullWeightSpeed"/> is the body speed in m/s at which this member carries
    /// its whole family. <paramref name="StrideMetres"/> is the distance one loop of the clip covers.
    /// <paramref name="SyncPhase"/> is the normalised time in <c>[0, 1)</c> of the clip's right-foot contact, so every
    /// clip in a blend puts that foot down together.</summary>
    public readonly record struct GaitClip(int ClipId, float FullWeightSpeed, float StrideMetres, float SyncPhase);

    /// <summary>One weighted clip written by <see cref="DirectionalLocomotionBlend.Advance"/>. <paramref name="Phase"/>
    /// is the normalised clip time in <c>[0, 1)</c> (multiply by the clip duration to sample it).
    /// <paramref name="Weight"/> is positive, and the weights written by one call sum to one.</summary>
    public readonly record struct GaitSample(int ClipId, float Phase, float Weight);
}
