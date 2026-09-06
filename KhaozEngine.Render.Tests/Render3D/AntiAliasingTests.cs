using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Pure (GPU-free) coverage of the unified AntiAliasing / RenderQuality settings API: the factory values, the
    // device-clamping in ResolveFor (never throws), and how a mode resolves into the low-level render config the
    // scene actually uses (EffectiveRenderScale / EffectiveSupersample / EffectiveFxaa / EffectiveMsaaSamples), incl.
    // the back-compat rule that AntiAliasing.Off leaves the raw RenderScale/Supersample fields untouched and that the
    // Pixelated retro path forces AA off.
    public sealed class AntiAliasingTests
    {
        static GpuCapabilities Caps(int maxMsaa) => new(false, true, "test", false, false, maxMsaa);

        [Fact]
        public void Msaa_can_keep_the_fxaa_post_filter_without_supersampling()
        {
            AntiAliasing aa = AntiAliasing.Msaa(2, postFxaa: true);
            Assert.NotEqual(AntiAliasing.Msaa(2), aa);
            Assert.True(aa.UsesFxaa);
            Assert.Equal(aa, aa.ResolveFor(Caps(4)));
            Assert.True(AntiAliasing.Msaa(8, postFxaa: true).ResolveFor(Caps(2)).UsesFxaa);
            Assert.Equal(AntiAliasing.Fxaa, aa.ResolveFor(Caps(1)));

            var s = new PixelPostProcessSettings();
            s.Quality.AntiAliasing = aa;
            Assert.True(s.EffectiveFxaa);
            Assert.Equal(2, s.EffectiveMsaaSamples);
            Assert.Equal((1600, 900), Scene3D.ComputeTargetSize(s, 1280, 720));
            s.Pixelated = true;
            Assert.False(s.EffectiveFxaa);
            Assert.Equal(1, s.EffectiveMsaaSamples);
        }

        [Fact]
        public void Factories_carry_the_right_mode_and_parameter()
        {
            Assert.Equal(AntiAliasingMode.None, AntiAliasing.Off.Mode);
            Assert.Equal(AntiAliasingMode.Fxaa, AntiAliasing.Fxaa.Mode);
            Assert.Equal(AntiAliasingMode.Msaa, AntiAliasing.Msaa(4).Mode);
            Assert.Equal(4, AntiAliasing.Msaa(4).MsaaSamples);
            Assert.Equal(AntiAliasingMode.Ssaa, AntiAliasing.Ssaa(3f).Mode);
            Assert.Equal(3f, AntiAliasing.Ssaa(3f).SsaaFactor);
            // Factories clamp nonsense params to the safe floor rather than throwing.
            Assert.Equal(1, AntiAliasing.Msaa(0).MsaaSamples);
            Assert.Equal(1f, AntiAliasing.Ssaa(0.2f).SsaaFactor);
        }

        [Fact]
        public void ResolveFor_clamps_msaa_down_to_the_device_max_power_of_two()
        {
            Assert.Equal(4, AntiAliasing.Msaa(8).ResolveFor(Caps(4)).MsaaSamples);   // 8 requested, device max 4
            Assert.Equal(4, AntiAliasing.Msaa(4).ResolveFor(Caps(4)).MsaaSamples);   // exactly the max
            Assert.Equal(2, AntiAliasing.Msaa(3).ResolveFor(Caps(8)).MsaaSamples);   // 3 -> largest pow2 <= 3 = 2
            Assert.Equal(8, AntiAliasing.Msaa(16).ResolveFor(Caps(8)).MsaaSamples);  // clamps to device max 8
        }

        [Fact]
        public void ResolveFor_falls_back_to_fxaa_when_the_device_cannot_msaa()
        {
            AntiAliasing r = AntiAliasing.Msaa(4).ResolveFor(Caps(1)); // no MSAA support
            Assert.Equal(AntiAliasingMode.Fxaa, r.Mode);
        }

        [Fact]
        public void ResolveFor_leaves_none_fxaa_ssaa_unchanged()
        {
            Assert.Equal(AntiAliasing.Off, AntiAliasing.Off.ResolveFor(Caps(1)));
            Assert.Equal(AntiAliasing.Fxaa, AntiAliasing.Fxaa.ResolveFor(Caps(1)));
            Assert.Equal(AntiAliasing.Ssaa(3f), AntiAliasing.Ssaa(3f).ResolveFor(Caps(1)));
        }

        [Fact]
        public void Default_settings_leave_the_raw_fields_authoritative()
        {
            var s = new PixelPostProcessSettings(); // Quality.AntiAliasing == Off
            Assert.Equal(AntiAliasingMode.None, s.EffectiveAaMode);
            Assert.Equal(RenderScale.FixedInternal, s.EffectiveRenderScale);
            Assert.Equal(1f, s.EffectiveSupersample);
            Assert.False(s.EffectiveFxaa);
            Assert.Equal(1, s.EffectiveMsaaSamples);

            // A consumer setting the LOW-LEVEL fields directly with AA Off is unchanged (Ruinborne's current path).
            var raw = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport, Supersample = 2f };
            Assert.Equal(RenderScale.MatchViewport, raw.EffectiveRenderScale);
            Assert.Equal(2f, raw.EffectiveSupersample);
        }

        [Fact]
        public void Ssaa_mode_forces_matchviewport_and_drives_supersample()
        {
            var s = new PixelPostProcessSettings(); // starts FixedInternal
            s.Quality.AntiAliasing = AntiAliasing.Ssaa(3f);
            Assert.Equal(RenderScale.MatchViewport, s.EffectiveRenderScale); // SSAA overrides FixedInternal
            Assert.Equal(3f, s.EffectiveSupersample);
            // ...so ComputeTargetSize supersamples even though RenderScale is left FixedInternal.
            Assert.Equal((1920, 1080), Scene3D.ComputeTargetSize(s, 640, 360));
            Assert.True(Scene3D.WantsMipDownsample(s, 640, 360));
        }

        [Fact]
        public void Fxaa_and_Msaa_modes_do_not_supersample()
        {
            var fx = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport };
            fx.Quality.AntiAliasing = AntiAliasing.Fxaa;
            Assert.True(fx.EffectiveFxaa);
            Assert.Equal(1f, fx.EffectiveSupersample);
            Assert.False(Scene3D.WantsMipDownsample(fx, 1280, 720));   // 1:1, no mip downscale

            var ms = new PixelPostProcessSettings { RenderScale = RenderScale.MatchViewport };
            ms.Quality.AntiAliasing = AntiAliasing.Msaa(4);
            Assert.Equal(4, ms.EffectiveMsaaSamples);
            Assert.False(ms.EffectiveFxaa);
            Assert.False(Scene3D.WantsMipDownsample(ms, 1280, 720));
        }

        [Fact]
        public void Pixelated_forces_AA_off()
        {
            var s = new PixelPostProcessSettings { Pixelated = true };
            s.Quality.AntiAliasing = AntiAliasing.Ssaa(3f);
            Assert.Equal(AntiAliasingMode.None, s.EffectiveAaMode);   // retro path wins
            Assert.Equal(RenderScale.FixedInternal, s.EffectiveRenderScale);
            Assert.Equal(1f, s.EffectiveSupersample);
            Assert.False(s.EffectiveFxaa);

            var f = new PixelPostProcessSettings { Pixelated = true };
            f.Quality.AntiAliasing = AntiAliasing.Fxaa;
            Assert.False(f.EffectiveFxaa);
        }
    }
}
