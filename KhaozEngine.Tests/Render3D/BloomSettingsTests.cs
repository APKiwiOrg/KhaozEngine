using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// Pure (device-free) coverage of the bloom settings bag: defaults (off, per the byte-stable-off invariant) and
    /// the pass-decision logic the post chain reads (bloom off => zero extra passes). GPU pixel output is covered
    /// by the scene3d_bloom golden; the separable-blur/threshold math itself is covered by
    /// <see cref="BloomMathTests"/>.
    /// </summary>
    public sealed class BloomSettingsTests
    {
        [Fact]
        public void Default_is_off()
        {
            var s = new BloomSettings();
            Assert.False(s.Enabled);
        }

        [Fact]
        public void PixelPostProcessSettings_defaults_bloom_off()
        {
            Assert.False(new PixelPostProcessSettings().Bloom.Enabled);
        }

        [Fact]
        public void Default_knob_values_match_the_documented_tuning()
        {
            var s = new BloomSettings();
            Assert.Equal(0.7f, s.Threshold);
            Assert.Equal(0.15f, s.Knee);
            Assert.Equal(0.6f, s.Intensity);
            Assert.Equal(4, s.Radius);
        }

        [Fact]
        public void UseSmoothPreset_does_not_touch_bloom()
        {
            // UseSmoothPreset dials down the stylized retro chain (quantize/dither/outline/starfield/pixelated);
            // bloom is an independent opt-in the preset must not implicitly flip either way.
            var s = new PixelPostProcessSettings { Bloom = { Enabled = true } };
            s.UseSmoothPreset();
            Assert.True(s.Bloom.Enabled);

            var off = new PixelPostProcessSettings();
            off.UseSmoothPreset();
            Assert.False(off.Bloom.Enabled);
        }

        [Fact]
        public void Radius_can_be_set_to_zero_for_a_sharp_unblurred_glow()
        {
            var s = new BloomSettings { Radius = 0 };
            Assert.Equal(0, s.Radius);
        }

        [Fact]
        public void Settings_are_independently_mutable_per_instance()
        {
            // Each PixelPostProcessSettings owns its own BloomSettings instance (not a shared static default),
            // so mutating one scene's bloom knobs never leaks into another's.
            var a = new PixelPostProcessSettings();
            var b = new PixelPostProcessSettings();
            a.Bloom.Enabled = true;
            a.Bloom.Threshold = 0.4f;
            Assert.False(b.Bloom.Enabled);
            Assert.Equal(0.7f, b.Bloom.Threshold);
        }
    }
}
