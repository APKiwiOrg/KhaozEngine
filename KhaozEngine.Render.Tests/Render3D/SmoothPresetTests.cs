using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SmoothPresetTests
    {
        [Fact]
        public void Defaults_have_outline_off()
        {
            // The stylized cel/outline look is opt-in. A bare settings object leaves the depth/normal edge
            // outline off engine-wide (consumers that want the toon look set Outline = true). UseSmoothPreset
            // keeps it off too.
            Assert.False(new PixelPostProcessSettings().Outline);
            var s = new PixelPostProcessSettings { Outline = true };
            s.UseSmoothPreset();
            Assert.False(s.Outline);
        }

        [Fact]
        public void Smooth_preset_turns_off_the_stylized_passes()
        {
            var s = new PixelPostProcessSettings
            {
                CelBands = 4, Quantize = true, Dither = true, Outline = true, Starfield = true, Pixelated = true,
            };
            s.UseSmoothPreset();
            Assert.Equal(0, s.CelBands);
            Assert.False(s.Quantize);
            Assert.False(s.Dither);
            Assert.False(s.Outline);
            Assert.False(s.Starfield);
            Assert.False(s.Pixelated);
        }

        [Fact]
        public void Smooth_preset_leaves_lighting_untouched()
        {
            var s = new PixelPostProcessSettings();
            var key = s.LightColor;
            var ambient = s.AmbientColor;
            s.UseSmoothPreset();
            Assert.Equal(key, s.LightColor);
            Assert.Equal(ambient, s.AmbientColor);
        }
    }
}
