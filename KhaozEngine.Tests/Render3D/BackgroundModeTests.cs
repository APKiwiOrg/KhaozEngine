using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The Background enum is a DERIVED view over the Starfield / Sky.Enabled booleans, not a second source of
    /// truth. These pin the two properties that make that safe: every value round-trips through set-then-get, and
    /// normalizing an ambiguous both-true state reproduces the engine's long-standing sky-over-starfield
    /// precedence.
    /// </summary>
    public class BackgroundModeTests
    {
        [Fact]
        public void Default_is_starfield()
        {
            var s = new PixelPostProcessSettings();
            Assert.Equal(BackgroundMode.Starfield, s.Background);
        }

        [Theory]
        [InlineData(BackgroundMode.Solid)]
        [InlineData(BackgroundMode.Starfield)]
        [InlineData(BackgroundMode.Sky)]
        public void Set_then_get_round_trips(BackgroundMode mode)
        {
            var s = new PixelPostProcessSettings();
            s.Background = mode;
            Assert.Equal(mode, s.Background);
        }

        [Fact]
        public void Setting_a_mode_clears_the_others()
        {
            var s = new PixelPostProcessSettings();
            s.Background = BackgroundMode.Sky;
            Assert.True(s.Sky.Enabled);
            Assert.False(s.Starfield);

            s.Background = BackgroundMode.Starfield;
            Assert.False(s.Sky.Enabled);
            Assert.True(s.Starfield);

            s.Background = BackgroundMode.Solid;
            Assert.False(s.Sky.Enabled);
            Assert.False(s.Starfield);
        }

        [Fact]
        public void Both_booleans_set_resolves_to_sky_matching_legacy_precedence()
        {
            // Legacy precedence: the sky pass writes alpha 1 at background pixels, so the blit's starfield marker
            // never fired over sky. Sky wins. The getter encodes exactly that.
            var s = new PixelPostProcessSettings { Starfield = true };
            s.Sky.Enabled = true;
            Assert.Equal(BackgroundMode.Sky, s.Background);
        }

        [Fact]
        public void Normalizing_an_ambiguous_state_is_idempotent()
        {
            var s = new PixelPostProcessSettings { Starfield = true };
            s.Sky.Enabled = true;
            s.Background = s.Background;   // set(get(x)) normalizes to the resolved mode
            Assert.Equal(BackgroundMode.Sky, s.Background);
            Assert.True(s.Sky.Enabled);
            Assert.False(s.Starfield);
        }

        [Fact]
        public void Reassigning_the_sky_settings_instance_still_resolves()
        {
            // Sky is a reassignable field. The derived property reads through it, so a fresh instance is picked up
            // and there is no owner back-pointer to go stale. This is exactly what an authoritative-enum design
            // would have broken on, see the design doc.
            var s = new PixelPostProcessSettings();
            s.Background = BackgroundMode.Sky;      // sets Sky.Enabled = true AND Starfield = false
            s.Sky = new SkySettings { Enabled = false };
            Assert.Equal(BackgroundMode.Solid, s.Background);
        }
    }
}
