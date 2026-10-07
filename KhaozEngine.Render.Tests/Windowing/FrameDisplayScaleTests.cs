using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// Frame metrics as <see cref="AppWindow"/> fills them each frame (framebuffer, window coordinates and the OS
    /// content scale), followed through <see cref="UiViewport.Update(Frame)"/>, pointer mapping and the DpiFont
    /// re-bake cache. Windows reports window coordinates in pixels, so its framebuffer-to-window ratio is 1 and only
    /// the OS content scale carries the 150% setting.
    /// </summary>
    public sealed class FrameDisplayScaleTests
    {
        [Theory]
        [InlineData(1f, 1920, 1080)]
        [InlineData(1.5f, 1280, 720)]
        [InlineData(2f, 960, 540)]
        [InlineData(2.5f, 768, 432)]
        public void Pixel_window_coordinates_take_the_ui_scale_from_the_os_content_scale(
            float osScale, int logicalWidth, int logicalHeight)
        {
            var frame = new Frame();
            frame.SetMetrics(1920, 1080, 1920, 1080, osScale);
            var ui = new UiViewport();
            ui.Update(frame);

            Assert.Equal(osScale, frame.DpiScale);
            Assert.Equal(logicalWidth, frame.LogicalWidth);
            Assert.Equal(logicalHeight, frame.LogicalHeight);
            Assert.Equal(osScale, ui.DpiScale);
            Assert.Equal(logicalWidth, ui.Width);
            Assert.Equal(logicalHeight, ui.Height);
            Assert.Equal(new Rect(0, 0, 1920, 1080), ui.ContentBounds);
        }

        [Fact]
        public void Retina_backing_ratio_and_content_scale_agree_on_the_point_size()
        {
            var frame = new Frame();
            frame.SetMetrics(4112, 2658, 2056, 1329, 2f);
            var ui = new UiViewport();
            ui.Update(frame);

            Assert.Equal(2f, ui.DpiScale);
            Assert.Equal(2056, ui.Width);
            Assert.Equal(1329, ui.Height);
            Assert.Equal(4112, frame.Width);
            Assert.Equal(2658, frame.Height);
        }

        [Fact]
        public void Resizing_at_a_constant_os_scale_keeps_the_exact_scale_and_one_font_bake()
        {
            var frame = new Frame();
            var ui = new UiViewport();
            int bakes = 0;
            var fonts = new DpiRebakeCache<object>(_ => { bakes++; return new object(); }, _ => { });
            var sizes = new (int Width, int Height, int LogicalWidth, int LogicalHeight)[]
            {
                (1920, 1080, 1280, 720),
                (1921, 1081, 1281, 721),
                (301, 170, 201, 113),
                (1000, 601, 667, 401),
            };

            foreach (var size in sizes)
            {
                frame.SetMetrics(size.Width, size.Height, size.Width, size.Height, 1.5f);
                ui.Update(frame);
                fonts.For(ui.DpiScale);

                Assert.Equal(1.5f, ui.DpiScale);
                Assert.Equal(size.LogicalWidth, ui.Width);
                Assert.Equal(size.LogicalHeight, ui.Height);
            }

            Assert.Equal(1, bakes);
        }

        [Fact]
        public void Monitor_and_os_scale_changes_reach_the_viewport_on_the_next_frame()
        {
            var frame = new Frame();
            var ui = new UiViewport();
            int bakes = 0;
            var fonts = new DpiRebakeCache<object>(_ => { bakes++; return new object(); }, _ => { });

            frame.SetMetrics(1920, 1080, 1920, 1080, 1f);
            ui.Update(frame);
            fonts.For(ui.DpiScale);
            Assert.Equal(new Rect(0, 0, 1920, 1080), ui.DesignBounds);

            // Dragged onto a 200% 4K monitor: GLFW resizes the framebuffer and reports the new scale.
            frame.SetMetrics(3840, 2160, 3840, 2160, 2f);
            ui.Update(frame);
            fonts.For(ui.DpiScale);
            Assert.Equal(2f, ui.DpiScale);
            Assert.Equal(new Rect(0, 0, 1920, 1080), ui.DesignBounds);

            // OS setting changed from 200% to 125% with the window left at its pixel size.
            frame.SetMetrics(3840, 2160, 3840, 2160, 1.25f);
            ui.Update(frame);
            fonts.For(ui.DpiScale);
            Assert.Equal(1.25f, ui.DpiScale);
            Assert.Equal(new Rect(0, 0, 3072, 1728), ui.DesignBounds);

            Assert.Equal(3, bakes);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(0f)]
        [InlineData(-1.5f)]
        [InlineData(float.PositiveInfinity)]
        public void Unavailable_or_invalid_os_scale_keeps_the_framebuffer_to_window_ratio(float osScale)
        {
            var retina = new Frame();
            retina.SetMetrics(2048, 1280, 1024, 640, osScale);
            var pixels = new Frame();
            pixels.SetMetrics(1920, 1080, 1920, 1080, osScale);

            Assert.Equal(2f, retina.DpiScale);
            Assert.Equal(1024, retina.LogicalWidth);
            Assert.Equal(640, retina.LogicalHeight);
            Assert.Equal(1f, pixels.DpiScale);
            Assert.Equal(1920, pixels.LogicalWidth);
            Assert.Equal(1080, pixels.LogicalHeight);
        }

        [Fact]
        public void A_minimized_frame_leaves_the_last_viewport_in_place()
        {
            var frame = new Frame();
            var ui = new UiViewport();
            frame.SetMetrics(1920, 1080, 1920, 1080, 1.5f);
            ui.Update(frame);

            frame.SetMetrics(0, 0, 0, 0, 1.5f);
            ui.Update(frame);

            Assert.Equal(0, frame.LogicalWidth);
            Assert.Equal(0, frame.LogicalHeight);
            Assert.Equal(1.5f, ui.DpiScale);
            Assert.Equal(1280, ui.Width);
            Assert.Equal(720, ui.Height);
        }

        [Theory]
        [InlineData(1920, 1080, 1920, 1080, 1.5f, 960f, 540f, 640f, 360f)]
        [InlineData(4112, 2658, 2056, 1329, 2f, 2000f, 1000f, 1000f, 500f)]
        public void Pointer_positions_stay_framebuffer_based_and_round_trip_through_points(
            int framebufferWidth, int framebufferHeight, int windowWidth, int windowHeight, float osScale,
            float cursorX, float cursorY, float pointX, float pointY)
        {
            var frame = new Frame();
            frame.SetMetrics(framebufferWidth, framebufferHeight, windowWidth, windowHeight, osScale);
            var ui = new UiViewport();
            ui.Update(frame);
            var input = new InputState(Vector2.Zero,
                new Vector2((float)framebufferWidth / windowWidth, (float)framebufferHeight / windowHeight),
                new HashSet<Key>(), new HashSet<Key>(), new HashSet<Key>(),
                new HashSet<MouseButton>(), new HashSet<MouseButton>(),
                new Vector2(cursorX, cursorY), Vector2.Zero, 0f, framebufferWidth, framebufferHeight);
            var pointer = new Pointer();

            pointer.Update(input, ui);

            Assert.Equal(new Vector2(pointX, pointY), pointer.Position);
            Assert.Equal(new Vector2(cursorX, cursorY), ui.DesignToScreen(pointer.Position));
            Assert.Equal(new Vector2((float)framebufferWidth / windowWidth, (float)framebufferHeight / windowHeight),
                input.FramebufferScale);
        }
    }
}
