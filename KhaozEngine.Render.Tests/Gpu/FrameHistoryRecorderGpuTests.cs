using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using KhaozEngine.BugReports;
using KhaozEngine.BugReports.Client;
using KhaozEngine.BugReports.Client.Internal;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Gpu.Metal.Internal.ObjC;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;
using StbImageSharp;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The bug report recorder end to end on a real Metal device over a headless <c>CAMetalLayer</c>, with no
    /// window: real drawables made copyable by the standby screenshot tap, the frame's swapchain copy, the GPU
    /// downscale, the fenced readback ring, the encode worker and the blocking screenshot readback, all driven
    /// through <see cref="FramePhases"/> the way <see cref="AppWindow"/> drives them.
    /// <para>
    /// It sits in <c>NativeDeviceLifecycle</c> because it builds a whole device beside the suite's own.
    /// </para>
    /// </summary>
    [Collection("NativeDeviceLifecycle")]
    public sealed class FrameHistoryRecorderGpuTests
    {
        readonly ITestOutputHelper _out;
        public FrameHistoryRecorderGpuTests(ITestOutputHelper o) => _out = o;

        [GpuFact(RequiresCompletionFences = true)]
        public void A_recorder_over_a_real_device_records_the_clip_and_the_screenshot()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;
            RecordThroughALayerBackedDevice(_out, false);
        }

        [GpuFact(RequiresCompletionFences = true)]
        public void A_split_scene_preserves_the_real_drawable_clip_and_trigger_frame_screenshot()
        {
            if (!MetalDormancy.NativeDeviceAvailable(_out)) return;
            RecordThroughALayerBackedDevice(_out, true);
        }

        // Split out under the guard for the reason every Metal row in this assembly splits: CA1416 reads the
        // platform guard at the call site.
        [SupportedOSPlatform("macos")]
        static void RecordThroughALayerBackedDevice(ITestOutputHelper output, bool split)
        {
            CAMetalLayer layer = CAMetalLayer.New();
            if (layer.IsNull)
            {
                output.WriteLine("dormant: this macOS would not create a CAMetalLayer, so no drawable can be copied.");
                return;
            }

            const uint Width = 1280u;
            const uint Height = 720u;
            const int Frames = 20;
            const int SwitchFrame = 10;
            const int ScreenshotFrame = 18;
            Color first = Color.FromBytes(200, 40, 60, 255);
            Color second = Color.FromBytes(30, 160, 90, 255);
            // The screenshot frame and the frame after it each get a colour of their own, so a screenshot taken a
            // frame early or a frame late reads a different colour from the one the trigger frame rendered.
            Color trigger = Color.FromBytes(70, 90, 210, 255);
            Color after = Color.FromBytes(230, 200, 40, 255);
            TimeSpan tick = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 12);
            // A one-pixel outline drawn in the screenshot frame. The screenshot is 1:1, so it must come back sharp.
            var outline = new Rect(100, 50, 1080, 620);
            Color ClearColour(int frame) => frame < SwitchFrame ? first
                : frame < ScreenshotFrame ? second
                : frame == ScreenshotFrame ? trigger
                : after;

            // Ownership of the layer transfers into the device, as in MetalSwapchainGpuTests.
            GpuProviderDevice created = MetalGpuDevice.CreateForHost(
                new MetalSwapchainHost(layer, new MetalDrawableSize(Width, Height)), syncToVerticalBlank: false);
            var device = (MetalGpuDevice)created.Device;
            IGpuCommandList commands = device.Factory.CreateCommandList();
            var taps = new FrameCaptureTaps(device, device);
            var time = new SteppedTime();
            Frame windowFrame = NewFrame(commands);
            FrameHistoryRecorder? recorder = null;
            var sprites = new SpriteBatch(device, device.SwapchainFramebuffer!.Outputs);
            IGpuTexture whiteHandle = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            device.UpdateTexture(whiteHandle, new byte[] { 255, 255, 255, 255 }, 0, 0, 1, 1);
            var white = new Texture2D(whiteHandle, 1, 1);
            var primitives = new PrimitiveRenderer(white);
            var scene = new Scene3D(device, device.SwapchainFramebuffer!.Outputs);
            scene.Post.RenderScale = RenderScale.MatchViewport;
            scene.Post.Hdr.Enabled = false;
            scene.Post.Bloom.Enabled = false;
            scene.Post.Outline = false;
            scene.Post.Quantize = false;
            scene.Post.Starfield = false;
            scene.Post.Quality.AntiAliasing = AntiAliasing.Off;
            var prepared = new PreparePhaseSceneSubmission(device, scene);
            void Prepare(Frame frame, Color color)
            {
                if (!split) return;
                scene.Begin();
                scene.Post.BackgroundColor = color;
                scene.PrepareFrame();
                prepared.Prepare(frame);
            }
            try
            {
                if (split)
                    FramePhases.Run(windowFrame, true, device, commands, first,
                        f => Prepare(f, first), f => prepared.Render(f, device.SwapchainFramebuffer!),
                        null, tick.TotalMilliseconds, true);
                recorder = new FrameHistoryRecorder(new TapsCaptureHost(device, taps), new FrameHistoryOptions(), time);
                Assert.True(recorder.IsAvailable);
                recorder.Start();
                Assert.Equal(2, taps.Count);

                Task<ReportImage>? screenshot = null;
                long drainsBefore = device.Timeline.WaitForIdleCalls;
                long countedBefore = device.Counters.DrainCount;
                long clipDrains = -1;
                long clipCounted = -1;

                for (int i = 0; i < Frames; i++)
                {
                    if (i == ScreenshotFrame)
                    {
                        clipDrains = device.Timeline.WaitForIdleCalls - drainsBefore;
                        clipCounted = device.Counters.DrainCount - countedBefore;
                    }

                    int index = i;
                    FramePhases.Run(windowFrame, render: true, device, commands,
                        ClearColour(i), onPrepare: f => Prepare(f, ClearColour(index)),
                        onFrame: f =>
                        {
                            if (split) prepared.Render(f, device.SwapchainFramebuffer!);
                            if (index != ScreenshotFrame) return;
                            sprites.NewFrame(commands, (int)Width, (int)Height);
                            sprites.Begin();
                            primitives.DrawRect(sprites, outline, Color.White);
                            sprites.End();
                            screenshot = recorder.CaptureScreenshotAsync();
                        },
                        taps, tick.TotalMilliseconds, split);
                    time.Advance(tick);
                    // Paces the loop as a real 12 fps tick does, which leaves the worker a whole interval per
                    // encode. Without it this loop outruns the encoder and the queue drops frames by design.
                    Assert.True(recorder.Encoder.WaitIdle(TimeSpan.FromSeconds(5)), "a clip encode took over 5 s");
                }

                Assert.NotNull(screenshot);
                Assert.True(screenshot.Wait(TimeSpan.FromSeconds(5)), "the screenshot did not land within 5 s");
                ReportImage image = screenshot.Result;
                ReportClip clip = recorder.Freeze();

                output.WriteLine($"clip: {clip.Frames.Count} frames, clip drains {clipDrains} (counted "
                    + $"{clipCounted}), screenshot {image.MediaType} {image.Width}x{image.Height} "
                    + $"{image.Bytes.Length} bytes, encode drops {recorder.Encoder.Dropped}");

                // No drain anywhere in the clip frames. The screenshot frame's readback is the one allowed.
                Assert.Equal(0L, clipDrains);
                Assert.Equal(0L, clipCounted);

                Assert.True(clip.Frames.Count >= 10, $"the clip held {clip.Frames.Count} frames, expected 10 or more");
                (int clipWidth, int clipHeight) = CaptureSize.Fit((int)Width, (int)Height, new FrameHistoryOptions().LongEdge);
                TimeSpan previous = TimeSpan.Zero;
                foreach (ReportClipFrame frame in clip.Frames)
                {
                    Assert.Equal(clipWidth, frame.Width);
                    Assert.Equal(clipHeight, frame.Height);
                    Assert.True(frame.Offset >= previous, "clip offsets went backwards");
                    previous = frame.Offset;
                }

                AssertCentre(clip.Frames[0].Jpeg, clipWidth, clipHeight, first, 8, "the first clip frame");
                // The newest frame read back before the freeze may come from any frame after the switch.
                Assert.True(CentreMatches(clip.Frames[^1].Jpeg, clipWidth, clipHeight, second, 8)
                    || CentreMatches(clip.Frames[^1].Jpeg, clipWidth, clipHeight, trigger, 8)
                    || CentreMatches(clip.Frames[^1].Jpeg, clipWidth, clipHeight, after, 8),
                    "the last clip frame is not one rendered after the switch");

                Assert.Equal("image/png", image.MediaType);
                Assert.Equal((int)Width, image.Width);
                Assert.Equal((int)Height, image.Height);
                AssertCentre(image.Bytes, (int)Width, (int)Height, trigger, 0, "the screenshot");
                Assert.False(CentreMatches(image.Bytes, (int)Width, (int)Height, after, 8),
                    "the screenshot holds the frame after the trigger");
                AssertOutline(image.Bytes, (int)Width, (int)Height, trigger, outline);
            }
            finally
            {
                recorder?.Dispose();
                taps.Dispose();
                windowFrame.DisposeSubmission();
                commands.Dispose();
                device.WaitForIdle();
                prepared.Dispose();
                scene.Dispose();
                primitives.Dispose();
                white.Dispose();
                sprites.Dispose();
                device.Dispose();
            }
        }

        static void AssertCentre(ReadOnlyMemory<byte> encoded, int width, int height, Color expected, int tolerance,
            string what)
        {
            (int r, int g, int b) = Centre(encoded, width, height);
            (int er, int eg, int eb) = (Byte(expected.R), Byte(expected.G), Byte(expected.B));
            Assert.True(CentreMatches(encoded, width, height, expected, tolerance),
                $"{what} centre is ({r}, {g}, {b}), expected ({er}, {eg}, {eb}) within {tolerance}");
        }

        static bool CentreMatches(ReadOnlyMemory<byte> encoded, int width, int height, Color expected, int tolerance)
        {
            (int r, int g, int b) = Centre(encoded, width, height);
            return Math.Abs(r - Byte(expected.R)) <= tolerance && Math.Abs(g - Byte(expected.G)) <= tolerance
                && Math.Abs(b - Byte(expected.B)) <= tolerance;
        }

        static (int R, int G, int B) Centre(ReadOnlyMemory<byte> encoded, int width, int height)
        {
            ImageResult decoded = ImageResult.FromMemory(encoded.ToArray(), ColorComponents.RedGreenBlue);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            int at = ((height / 2) * width + width / 2) * 3;
            return (decoded.Data[at], decoded.Data[at + 1], decoded.Data[at + 2]);
        }

        // The whole decoded screenshot, byte for byte: the background everywhere except the one-pixel outline.
        static void AssertOutline(ReadOnlyMemory<byte> png, int width, int height, Color background, Rect outline)
        {
            ImageResult decoded = ImageResult.FromMemory(png.ToArray(), ColorComponents.RedGreenBlue);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            byte[] fill = { Byte(background.R), Byte(background.G), Byte(background.B) };
            int left = (int)outline.X, top = (int)outline.Y;
            int right = left + (int)outline.Width - 1, bottom = top + (int)outline.Height - 1;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    bool inside = x >= left && x <= right && y >= top && y <= bottom;
                    bool edge = inside && (x == left || x == right || y == top || y == bottom);
                    int at = (y * width + x) * 3;
                    for (int c = 0; c < 3; c++)
                    {
                        byte expected = edge ? (byte)255 : fill[c];
                        if (decoded.Data[at + c] == expected) continue;
                        Assert.Fail($"the screenshot pixel ({x},{y}) channel {c} was {decoded.Data[at + c]}, "
                            + $"expected {expected}");
                    }
                }
        }

        static byte Byte(float channel) => (byte)Math.Round(channel * 255f);

        static Frame NewFrame(IGpuCommandList commands) => new()
        {
            Dt = 1f / 12f,
            Width = 1280,
            Height = 720,
            LogicalWidth = 1280,
            LogicalHeight = 720,
            Commands = commands,
        };

        /// <summary>The window's side of the recorder, over a bare tap registry.</summary>
        sealed class TapsCaptureHost : IFrameCaptureHost
        {
            readonly IGpuDevice _device;
            readonly FrameCaptureTaps _taps;

            internal TapsCaptureHost(IGpuDevice device, FrameCaptureTaps taps)
            {
                _device = device;
                _taps = taps;
            }

            public bool SupportsClipCapture => AppWindowCaptureHost.ClipCaptureOn(_device);

            public bool SupportsSwapchainCopy => AppWindowCaptureHost.SwapchainCopyOn(_device);

            public void AddCaptureTap(IFrameCaptureTap tap) => _taps.Add(tap);

            public void RemoveCaptureTap(IFrameCaptureTap tap) => _taps.Remove(tap);
        }

        /// <summary>A clock the loop moves one clip interval per frame, so every frame claims a tick.</summary>
        sealed class SteppedTime : TimeProvider
        {
            long _timestamp;

            public override long TimestampFrequency => TimeSpan.TicksPerSecond;

            public override long GetTimestamp() => _timestamp;

            internal void Advance(TimeSpan by) => _timestamp += by.Ticks;
        }
    }
}
