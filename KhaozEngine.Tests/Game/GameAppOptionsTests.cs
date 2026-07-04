using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // The GameApp loop needs a real window so it is sample/golden-verified, not unit-tested. These cover the
    // pure, headless bits: the For() defaults + the design-size fallback (0 => Width/Height).
    public class GameAppOptionsTests
    {
        [Fact]
        public void For_SetsSensibleDefaults()
        {
            var o = GameAppOptions.For("Demo", 960, 540);

            Assert.Equal("Demo", o.Title);
            Assert.Equal(960, o.Width);
            Assert.Equal(540, o.Height);
            Assert.Equal(0, o.DesignWidth);
            Assert.Equal(0, o.DesignHeight);
            Assert.Equal(ScaleMode.Fit, o.ScaleMode);
            Assert.Equal(new Color(0.10f, 0.12f, 0.16f, 1f), o.ClearColor);
            // No factories by default -> GameApp builds a plain AppWindow + DesignViewport.
            Assert.Null(o.WindowFactory);
            Assert.Null(o.ViewportFactory);
            // Resume hook armed by default at a safe 30s so a normal frame / GC pause never trips it.
            Assert.Equal(30.0, o.ResumeGapThresholdSeconds);
            // No Windows taskbar identity by default -> keeps the current process-derived AppUserModelID.
            Assert.Null(o.AppUserModelId);
            // Present defaults preserve current behaviour: vsync on, no software frame cap (uncapped).
            Assert.Equal(PresentMode.Vsync, o.PresentMode);
            Assert.Equal(0, o.FrameCapHz);
        }

        [Fact]
        public void PresentMode_and_FrameCapHz_RoundTrip_WhenSet()
        {
            var o = GameAppOptions.For("Demo", 960, 540);
            o.PresentMode = PresentMode.Immediate;
            o.FrameCapHz = 120;

            Assert.Equal(PresentMode.Immediate, o.PresentMode);
            Assert.Equal(120, o.FrameCapHz);
        }

        [Fact]
        public void AppUserModelId_RoundTrips_WhenSet()
        {
            var o = GameAppOptions.For("Demo", 960, 540);
            o.AppUserModelId = "APKiwi.Nullwake";

            Assert.Equal("APKiwi.Nullwake", o.AppUserModelId);
        }

        [Fact]
        public void DesignSize_FallsBackToWindowSize_WhenZero()
        {
            var o = GameAppOptions.For("Demo", 1280, 720);   // DesignWidth/Height left at 0

            Assert.Equal(1280, o.ResolvedDesignWidth);
            Assert.Equal(720, o.ResolvedDesignHeight);
        }

        [Fact]
        public void DesignSize_UsesExplicitValues_WhenNonZero()
        {
            var o = GameAppOptions.For("Demo", 1920, 1080);
            o.DesignWidth = 960;
            o.DesignHeight = 540;

            Assert.Equal(960, o.ResolvedDesignWidth);
            Assert.Equal(540, o.ResolvedDesignHeight);
        }

        [Fact]
        public void DesignSize_FallsBackPerAxis_WhenOnlyOneZero()
        {
            var o = GameAppOptions.For("Demo", 800, 600);
            o.DesignWidth = 400;   // height left at 0

            Assert.Equal(400, o.ResolvedDesignWidth);
            Assert.Equal(600, o.ResolvedDesignHeight);
        }
    }
}
