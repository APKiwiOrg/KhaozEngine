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

            RecordThroughALayerBackedDevice(_out);
        }

        // Split out under the guard for the reason every Metal row in this assembly splits: CA1416 reads the
        // platform guard at the call site.
        [SupportedOSPlatform("macos")]
        static void RecordThroughALayerBackedDevice(ITestOutputHelper output)
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
            TimeSpan tick = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 12);

            // Ownership of the layer transfers into the device, as in MetalSwapchainGpuTests.
            GpuProviderDevice created = MetalGpuDevice.CreateForHost(
                new MetalSwapchainHost(layer, new MetalDrawableSize(Width, Height)), syncToVerticalBlank: false);
            var device = (MetalGpuDevice)created.Device;
            IGpuCommandList commands = device.Factory.CreateCommandList();
            var taps = new FrameCaptureTaps(device, device);
            var time = new SteppedTime();
            FrameHistoryRecorder? recorder = null;

            try
            {
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
                    FramePhases.Run(NewFrame(commands), render: true, device, commands,
                        i < SwitchFrame ? first : second, onPrepare: null,
                        onFrame: _ =>
                        {
                            if (index == ScreenshotFrame) screenshot = recorder.CaptureScreenshotAsync();
                        },
                        taps, tick.TotalMilliseconds);
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
                AssertCentre(clip.Frames[^1].Jpeg, clipWidth, clipHeight, second, 8, "the last clip frame");

                Assert.Equal("image/png", image.MediaType);
                Assert.Equal((int)Width, image.Width);
                Assert.Equal((int)Height, image.Height);
                AssertCentre(image.Bytes, (int)Width, (int)Height, second, 2, "the screenshot");
            }
            finally
            {
                recorder?.Dispose();
                taps.Dispose();
                commands.Dispose();
                device.WaitForIdle();
                device.Dispose();
            }
        }

        static void AssertCentre(ReadOnlyMemory<byte> encoded, int width, int height, Color expected, int tolerance,
            string what)
        {
            ImageResult decoded = ImageResult.FromMemory(encoded.ToArray(), ColorComponents.RedGreenBlue);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height, decoded.Height);
            int at = ((height / 2) * width + width / 2) * 3;
            (int r, int g, int b) = (decoded.Data[at], decoded.Data[at + 1], decoded.Data[at + 2]);
            (int er, int eg, int eb) = ((int)Math.Round(expected.R * 255f), (int)Math.Round(expected.G * 255f),
                (int)Math.Round(expected.B * 255f));
            Assert.True(Math.Abs(r - er) <= tolerance && Math.Abs(g - eg) <= tolerance && Math.Abs(b - eb) <= tolerance,
                $"{what} centre is ({r}, {g}, {b}), expected ({er}, {eg}, {eb}) within {tolerance}");
        }

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

            public bool SupportsClipCapture => _device.Capabilities.SupportsCompletionFences
                && _device is IGpuSwapchainCapture && _device is IGpuFencedReadback;

            public bool SupportsSwapchainCopy => _device is IGpuSwapchainCapture;

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
