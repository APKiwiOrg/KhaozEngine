using System;
using System.Collections.Generic;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Headless coverage for <see cref="WindowIcon"/> (the decode-free RGBA8 icon the windowing layer accepts) and
    /// <see cref="AppWindow.ToRawImages"/> (the pure WindowIcon -> Silk RawImage mapping that feeds GLFW's
    /// SetWindowIcon). The actual glfwSetWindowIcon call + the macOS no-op need a real window, so they stay out.
    /// </summary>
    public sealed class WindowIconTests
    {
        static byte[] Rgba(int w, int h)
        {
            var p = new byte[w * h * 4];
            for (int i = 0; i < p.Length; i++) p[i] = (byte)(i & 0xFF);
            return p;
        }

        [Fact]
        public void Holds_tightly_packed_rgba_and_dimensions()
        {
            var px = Rgba(4, 3);
            var icon = new WindowIcon(px, 4, 3);

            Assert.Equal(4, icon.Width);
            Assert.Equal(3, icon.Height);
            Assert.Same(px, icon.Pixels);
        }

        [Fact]
        public void Rejects_pixels_that_are_not_width_times_height_times_four()
        {
            Assert.Throws<ArgumentException>(() => new WindowIcon(new byte[10], 4, 3));
        }

        [Fact]
        public void Rejects_non_positive_dimensions()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new WindowIcon(new byte[0], 0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new WindowIcon(new byte[0], 1, 0));
        }

        [Fact]
        public void Rejects_null_pixels()
        {
            Assert.Throws<ArgumentNullException>(() => new WindowIcon(null!, 2, 2));
        }

        [Fact]
        public void ToRawImages_maps_each_icon_preserving_size_and_pixels()
        {
            var a = new WindowIcon(Rgba(2, 2), 2, 2);
            var b = new WindowIcon(Rgba(4, 4), 4, 4);

            var raw = AppWindow.ToRawImages(new[] { a, b });

            Assert.Equal(2, raw.Length);
            Assert.Equal(2, raw[0].Width);
            Assert.Equal(2, raw[0].Height);
            Assert.Equal(4, raw[1].Width);
            Assert.Equal(4, raw[1].Height);
            Assert.True(raw[0].Pixels.Span.SequenceEqual(a.Pixels));
            Assert.True(raw[1].Pixels.Span.SequenceEqual(b.Pixels));
        }

        [Fact]
        public void ToRawImages_on_an_empty_list_yields_an_empty_array_so_SetIcon_no_ops()
        {
            Assert.Empty(AppWindow.ToRawImages(Array.Empty<WindowIcon>()));
        }
    }
}
