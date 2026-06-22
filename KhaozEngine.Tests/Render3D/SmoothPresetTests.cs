using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    public class SmoothPresetTests
    {
        [Fact]
        public void Smooth_preset_turns_off_the_stylized_passes()
        {
            var s = new PixelPostProcessSettings
            {
                CelBands = 4, Quantize = true, Dither = true, Outline = true, Starfield = true,
            };
            s.UseSmoothPreset();
            Assert.Equal(0, s.CelBands);
            Assert.False(s.Quantize);
            Assert.False(s.Dither);
            Assert.False(s.Outline);
            Assert.False(s.Starfield);
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
