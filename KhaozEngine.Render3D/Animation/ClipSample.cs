namespace KhaozEngine.Render3D
{
    /// <summary>One weighted clip in <see cref="AnimationSampler.SampleBlendInto"/>: <paramref name="Clip"/> sampled at
    /// <paramref name="Time"/> clip seconds, contributing in proportion to <paramref name="Weight"/> (finite, not
    /// negative). A zero-weight sample is skipped and its clip may be null.</summary>
    public readonly record struct ClipSample(AnimationClip Clip, float Time, float Weight);
}
