using System;
using KhaozEngine.Render3D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// <see cref="TemporalShimmer"/>: the shimmer record averages at one over the jitter phase count, and the GLSL
/// <c>temporalShimmerRate</c> mirrors <see cref="TemporalJitter.PhaseCount"/> with the same three constants.
/// </summary>
public sealed class TemporalShimmerTests
{
    [Theory]
    [InlineData(1f), InlineData(1.5f), InlineData(1.7f), InlineData(2f), InlineData(3f), InlineData(4f), InlineData(5f)]
    [InlineData(0.5f), InlineData(float.NaN)]
    public void The_shimmer_rate_is_one_over_the_jitter_phase_count(float ratio) =>
        Assert.Equal(1f / TemporalJitter.PhaseCount(ratio), TemporalShimmer.Rate(ratio));

    [Fact]
    public void The_shader_rate_names_the_jitter_sequence_constants()
    {
        string glsl = ShaderSources.TemporalShimmerGlsl;
        Assert.Contains($"const float JitterNativePhaseCount = {TemporalJitter.NativePhaseCount}.0;", glsl,
            StringComparison.Ordinal);
        Assert.Contains($"const float JitterMaxDisplayOverInternalRatio = {TemporalJitter.MaxDisplayOverInternalRatio}.0;",
            glsl, StringComparison.Ordinal);
        Assert.Contains($"const float JitterMaxPhaseCount = {TemporalJitter.MaxPhaseCount}.0;", glsl,
            StringComparison.Ordinal);
        Assert.Contains("float temporalShimmerRate() {", glsl, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_entry_points_carry_the_shimmer_record_into_the_history_state()
    {
        const string store = "oState = vec4(p.confidence, temporalStoreLock(p.stability, p.moved), p.drift, p.swing);";
        foreach (string program in new[] { ShaderSources.TemporalResolveFrag, ShaderSources.TemporalAccumulateFrag })
        {
            Assert.Contains(ShaderSources.TemporalShimmerGlsl, program, StringComparison.Ordinal);
            Assert.Contains(store, program, StringComparison.Ordinal);
        }
    }
}
