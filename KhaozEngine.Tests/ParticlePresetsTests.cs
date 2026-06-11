using Microsoft.Xna.Framework;
using KhaozEngine.Effects;
using Xunit;

namespace KhaozEngine.Tests;

public class ParticlePresetsTests
{
    [Fact]
    public void Spark_matches_nullwake_values()
    {
        var s = ParticlePresets.Spark;
        Assert.Equal(40f, s.MinSpeed);
        Assert.Equal(80f, s.MaxSpeed);
        Assert.Equal(0.22f, s.MinLife);
        Assert.Equal(0.35f, s.MaxLife);
        Assert.Equal(2f, s.StartSize);
        Assert.Equal(1f, s.EndSizeFactor);
        Assert.Equal(ParticleEmission.Radial, s.Emission);
        Assert.Equal(3f, s.JitterX);
        Assert.Equal(3f, s.JitterY);
        Assert.Equal(Color.White, s.BlendTarget);
        Assert.Equal(0.5f, s.BlendAmount);
        Assert.Null(s.OverrideColor);
    }

    [Fact]
    public void Ember_matches_nullwake_values()
    {
        var e = ParticlePresets.Ember;
        Assert.Equal(15f, e.MinSpeed);
        Assert.Equal(25f, e.MaxSpeed);
        Assert.Equal(0.45f, e.MinLife);
        Assert.Equal(0.7f, e.MaxLife);
        Assert.Equal(3f, e.StartSize);
        Assert.Equal(0.3f, e.EndSizeFactor);
        Assert.Equal(ParticleEmission.Directional, e.Emission);
        Assert.Equal(new Vector2(0f, -1f), e.Direction);
        Assert.Equal(5f, e.JitterX);
        Assert.Equal(3f, e.JitterY);
        Assert.Equal(6f, e.SwayFrequency);
        Assert.Equal(8f, e.SwayAmplitude);
        Assert.Equal(new Color(255, 160, 40), e.OverrideColor);
    }
}
