using System;
using KhaozEngine.Game;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless coverage for the Game-layer icon resolution (<see cref="GameApp.ResolveWindowIcons"/>): explicit
    /// <see cref="GameAppOptions.WindowIcons"/> win over <see cref="GameAppOptions.WindowIconPath"/>, and no icon
    /// configured resolves to an empty set (SetIcon then no-ops). The PNG-decode-from-path branch goes through
    /// <see cref="ImageRgba.Load"/> and is exercised by consumers, not unit-tested here (it touches the disk).
    /// </summary>
    public sealed class GameAppOptionsIconTests
    {
        static ImageRgba Img(int w, int h) => new ImageRgba(new byte[w * h * 4], w, h);

        [Fact]
        public void No_icon_configured_resolves_to_empty()
        {
            var o = GameAppOptions.For("t", 320, 240);
            Assert.Empty(GameApp.ResolveWindowIcons(o));
        }

        [Fact]
        public void Explicit_WindowIcons_are_mapped_to_window_icons()
        {
            var o = GameAppOptions.For("t", 320, 240);
            o.WindowIcons = new[] { Img(16, 16), Img(32, 32) };

            var icons = GameApp.ResolveWindowIcons(o);

            Assert.Equal(2, icons.Length);
            Assert.Equal(16, icons[0].Width);
            Assert.Equal(32, icons[1].Width);
        }

        [Fact]
        public void Explicit_WindowIcons_take_priority_over_a_path()
        {
            var o = GameAppOptions.For("t", 320, 240);
            o.WindowIcons = new[] { Img(48, 48) };
            o.WindowIconPath = "/no/such/file/should/be/read.png"; // must NOT be loaded when icons are explicit

            var icons = GameApp.ResolveWindowIcons(o);

            Assert.Single(icons);
            Assert.Equal(48, icons[0].Width);
        }
    }
}
