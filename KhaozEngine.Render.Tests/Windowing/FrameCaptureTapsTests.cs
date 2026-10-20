using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The frame capture tap registry and the frame shape it adds to <see cref="FramePhases"/>: taps record inside
    /// the frame's list after the game's draws, a tap's fence rides the frame's one submit, and the after-submit
    /// callbacks run between submit and present with nothing recording. With no tap the frame is unchanged.
    /// Driven on <see cref="OpenListTrackingGpuDevice"/>, so no GPU is needed.
    /// </summary>
    public sealed class FrameCaptureTapsTests
    {
        const double FrameMs = 16.7;

        static Frame NewFrame(IGpuCommandList commands, bool renderSuppressed = false) => new()
        {
            Dt = 1f / 60f,
            Width = 320,
            Height = 200,
            LogicalWidth = 320,
            LogicalHeight = 200,
            Commands = commands,
            RenderSuppressed = renderSuppressed,
        };

        static void RunFrame(OpenListTrackingGpuDevice device, IGpuCommandList list, FrameCaptureTaps? taps,
            Action<Frame>? onFrame = null, bool render = true)
            => FramePhases.Run(NewFrame(list, renderSuppressed: !render), render, device, list, Color.Black,
                onPrepare: null, onFrame: onFrame ?? (_ => { }), taps, FrameMs);

        [Fact]
        public void With_no_tap_registered_a_frame_submits_once_without_a_fence_then_presents()
        {
            using (var device = new OpenListTrackingGpuDevice(completionFences: true))
            using (IGpuCommandList list = device.Factory.CreateCommandList())
            {
                RunFrame(device, list, taps: null);
                Assert.Equal(new[] { "submit", "present" }, device.Order);
                Assert.Equal(0, device.FencedSubmits);
            }

            using (var device = new OpenListTrackingGpuDevice(completionFences: true))
            using (IGpuCommandList list = device.Factory.CreateCommandList())
            using (var taps = new FrameCaptureTaps(device, new FakeSwapchainCapture()))
            {
                RunFrame(device, list, taps);
                Assert.Equal(new[] { "submit", "present" }, device.Order);
                Assert.Equal(0, device.FencedSubmits);
                Assert.False(taps.HasWork);
            }
        }

        [Fact]
        public void A_tap_records_inside_the_frame_list_after_the_game_draws()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            using var taps = new FrameCaptureTaps(device, null);
            var log = new List<string>();
            var tap = new RecordingTap(device, log);
            FrameCaptureContext? seen = null;
            tap.OnRecord = c => seen = c;
            taps.Add(tap);

            RunFrame(device, list, taps, onFrame: _ => log.Add("frame"));

            Assert.Equal("frame", log[0]);
            Assert.Equal("record", log[1]);
            Assert.Equal(new[] { 1 }, tap.OpenDuringRecord);
            Assert.NotNull(seen);
            Assert.Same(list, seen!.Commands);
            Assert.Same(device, seen.Device);
            Assert.Equal(FrameMs, seen.FrameMilliseconds);
        }

        [Fact]
        public void AfterSubmit_runs_with_nothing_recording_between_submit_and_present()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            using var taps = new FrameCaptureTaps(device, null);
            var tap = new RecordingTap(device, new List<string>());
            tap.OnAfter = _ => device.Order.Add("after");
            taps.Add(tap);

            RunFrame(device, list, taps);

            Assert.Equal(new[] { "submit", "after", "present" }, device.Order);
            Assert.Equal(new[] { 0 }, tap.OpenDuringAfter);
        }

        [Fact]
        public void A_fence_handed_in_by_a_tap_rides_the_frame_submit()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            using IGpuFence fence = device.Factory.CreateFence();
            using var taps = new FrameCaptureTaps(device, null);
            var tap = new RecordingTap(device, new List<string>());
            tap.OnRecord = c => c.SignalOnSubmit(fence);
            taps.Add(tap);

            RunFrame(device, list, taps);

            Assert.Equal(1, device.FencedSubmits);
            Assert.Equal(1, device.Submits);
            Assert.Equal(new[] { "submit+fence", "present" }, device.Order);
        }

        [Fact]
        public void A_second_fence_in_one_frame_is_refused()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            using IGpuFence first = device.Factory.CreateFence();
            using IGpuFence second = device.Factory.CreateFence();
            using var taps = new FrameCaptureTaps(device, null);
            var a = new RecordingTap(device, new List<string>()) { OnRecord = c => c.SignalOnSubmit(first) };
            var b = new RecordingTap(device, new List<string>()) { OnRecord = c => c.SignalOnSubmit(second) };
            taps.Add(a);
            taps.Add(b);

            Assert.Throws<InvalidOperationException>(() => RunFrame(device, list, taps));
            Assert.Equal(0, device.OpenLists);
            Assert.Equal(0, device.Submits);
        }

        [Fact]
        public void A_render_suppressed_frame_runs_no_tap()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            using var taps = new FrameCaptureTaps(device, null);
            var log = new List<string>();
            taps.Add(new RecordingTap(device, log));

            RunFrame(device, list, taps, render: false);

            Assert.DoesNotContain("record", log);
            Assert.DoesNotContain("after", log);
            Assert.Equal(0, device.Submits);
            Assert.Equal(0, device.Presents);
        }

        [Fact]
        public void The_swapchain_is_copied_once_per_frame_however_many_taps_ask()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true, swapchain: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            var capture = new FakeSwapchainCapture { SwapchainCopyable = true };
            using var taps = new FrameCaptureTaps(device, capture);
            IGpuTexture? seenByA = null, seenByB = null;
            taps.Add(new RecordingTap(device, new List<string>()) { OnRecord = c => seenByA = c.AcquireSwapchainImage() });
            taps.Add(new RecordingTap(device, new List<string>()) { OnRecord = c => seenByB = c.AcquireSwapchainImage() });

            RunFrame(device, list, taps);

            Assert.NotNull(seenByA);
            Assert.Same(seenByA, seenByB);
            Assert.Equal(1, capture.Copies);
            Assert.Equal(320u, seenByA!.Width);
            Assert.Equal(200u, seenByA.Height);
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, seenByA.Format);
            Assert.Same(seenByA, capture.LastDestination);
        }

        [Fact]
        public void An_uncopyable_swapchain_gives_every_tap_null()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true, swapchain: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            var capture = new FakeSwapchainCapture { SwapchainCopyable = false };
            using var taps = new FrameCaptureTaps(device, capture);
            var seen = new List<IGpuTexture?>();
            taps.Add(new RecordingTap(device, new List<string>()) { OnRecord = c => seen.Add(c.AcquireSwapchainImage()) });
            taps.Add(new RecordingTap(device, new List<string>()) { OnRecord = c => seen.Add(c.AcquireSwapchainImage()) });

            RunFrame(device, list, taps);

            Assert.Equal(new IGpuTexture?[] { null, null }, seen);
            Assert.Equal(0, capture.Copies);
            Assert.Equal(0, device.TexturesCreated);
        }

        [Fact]
        public void Copy_source_usage_is_requested_with_the_first_tap_and_released_after_the_last()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            var capture = new FakeSwapchainCapture();
            using var taps = new FrameCaptureTaps(device, capture);
            var a = new RecordingTap(device, new List<string>());
            var b = new RecordingTap(device, new List<string>());

            taps.Add(a);
            taps.Add(b);
            taps.Remove(a);
            taps.Remove(b);

            Assert.Equal(new[] { true, false }, capture.Requested);
            Assert.Equal(0, taps.Count);
        }

        [Fact]
        public void A_tap_may_remove_itself_from_inside_Record()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            using var taps = new FrameCaptureTaps(device, null);
            var leaving = new RecordingTap(device, new List<string>());
            leaving.OnRecord = _ => taps.Remove(leaving);
            var staying = new RecordingTap(device, new List<string>());
            taps.Add(leaving);
            taps.Add(staying);

            RunFrame(device, list, taps);

            Assert.Equal(1, leaving.Detaches);
            Assert.Equal(1, leaving.Records);
            Assert.Equal(0, leaving.Afters);
            Assert.Equal(1, staying.Records);
            Assert.Equal(1, staying.Afters);

            RunFrame(device, list, taps);

            Assert.Equal(1, leaving.Detaches);
            Assert.Equal(1, leaving.Records);
            Assert.Equal(2, staying.Records);
            Assert.Equal(1, taps.Count);
        }

        /// <summary>
        /// The last tap may remove itself and then still use this frame's image. The image made after that removal
        /// has no tap left to release it, so the registry retires it at the end of the frame, and once the retire
        /// queue has drained the frame is the ordinary one again.
        /// </summary>
        [Fact]
        public void A_last_tap_that_removes_itself_then_acquires_leaves_no_texture_alive()
        {
            using var device = new OpenListTrackingGpuDevice(swapchain: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            var capture = new FakeSwapchainCapture { SwapchainCopyable = true };
            using var taps = new FrameCaptureTaps(device, capture);
            IGpuTexture? image = null;
            var last = new RecordingTap(device, new List<string>());
            last.OnRecord = c =>
            {
                taps.Remove(last);
                image = c.AcquireSwapchainImage();
            };
            taps.Add(last);

            RunFrame(device, list, taps);

            Assert.NotNull(image);
            Assert.Equal(1, capture.Copies);
            Assert.Equal(0, taps.Count);
            Assert.Equal(new[] { true, false }, capture.Requested);

            for (int frame = 0; frame < 8 && taps.HasWork; frame++) RunFrame(device, list, taps);

            Assert.False(taps.HasWork);
            Assert.Equal(0, device.TexturesAlive);
            device.Order.Clear();
            RunFrame(device, list, taps);
            Assert.Equal(new[] { "submit", "present" }, device.Order);
        }

        [Fact]
        public void Disposing_the_registry_detaches_every_tap()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true, swapchain: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            var capture = new FakeSwapchainCapture { SwapchainCopyable = true };
            var taps = new FrameCaptureTaps(device, capture);
            IGpuTexture owned = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            var a = new RecordingTap(device, new List<string>())
            {
                OnRecord = c => c.AcquireSwapchainImage(),
                OnDetach = retire => retire(owned),
            };
            var b = new RecordingTap(device, new List<string>());
            taps.Add(a);
            taps.Add(b);
            RunFrame(device, list, taps);

            taps.Dispose();

            Assert.Equal(1, a.Detaches);
            Assert.Equal(1, b.Detaches);
            Assert.Equal(0, taps.Count);
            Assert.Equal(new[] { true, false }, capture.Requested);
            Assert.Equal(0, device.TexturesAlive);   // the tap's texture and the swapchain image copy
        }

        [Fact]
        public void A_throwing_tap_Detach_does_not_stop_the_registry_from_detaching_the_rest()
        {
            using var device = new OpenListTrackingGpuDevice(completionFences: true, swapchain: true);
            using IGpuCommandList list = device.Factory.CreateCommandList();
            var capture = new FakeSwapchainCapture { SwapchainCopyable = true };
            var taps = new FrameCaptureTaps(device, capture);
            IGpuTexture owned = device.Factory.CreateTexture(
                GpuTextureDescription.Texture2D(4, 4, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            var throwing = new RecordingTap(device, new List<string>())
            {
                OnRecord = c => c.AcquireSwapchainImage(),
                OnDetach = _ => throw new InvalidOperationException("detach failed"),
            };
            var after = new RecordingTap(device, new List<string>()) { OnDetach = retire => retire(owned) };
            taps.Add(throwing);
            taps.Add(after);
            RunFrame(device, list, taps);

            var thrown = Assert.Throws<InvalidOperationException>(taps.Dispose);

            Assert.Equal("detach failed", thrown.Message);
            Assert.Equal(1, throwing.Detaches);
            Assert.Equal(1, after.Detaches);
            Assert.Equal(new[] { true, false }, capture.Requested);
            Assert.Equal(0, device.TexturesAlive);   // the retire queue was still flushed
            Assert.False(taps.HasWork);
        }

        /// <summary>Logs each callback and how many lists were open during it, and runs an optional action.</summary>
        sealed class RecordingTap : IFrameCaptureTap
        {
            readonly OpenListTrackingGpuDevice _device;
            readonly List<string> _log;

            internal RecordingTap(OpenListTrackingGpuDevice device, List<string> log)
            {
                _device = device;
                _log = log;
            }

            internal Action<FrameCaptureContext>? OnRecord { get; set; }
            internal Action<FrameCaptureContext>? OnAfter { get; set; }
            internal Action<Action<IDisposable>>? OnDetach { get; set; }
            internal List<int> OpenDuringRecord { get; } = new();
            internal List<int> OpenDuringAfter { get; } = new();
            internal int Records => OpenDuringRecord.Count;
            internal int Afters => OpenDuringAfter.Count;
            internal int Detaches { get; private set; }

            public void Record(FrameCaptureContext context)
            {
                _log.Add("record");
                OpenDuringRecord.Add(_device.OpenLists);
                OnRecord?.Invoke(context);
            }

            public void AfterSubmit(FrameCaptureContext context)
            {
                _log.Add("after");
                OpenDuringAfter.Add(_device.OpenLists);
                OnAfter?.Invoke(context);
            }

            public void Detach(Action<IDisposable> retire)
            {
                Detaches++;
                OnDetach?.Invoke(retire);
            }
        }

        /// <summary>A swapchain capture with no GPU behind it: it counts copies and logs every usage request.</summary>
        sealed class FakeSwapchainCapture : IGpuSwapchainCapture
        {
            bool _requested;

            internal List<bool> Requested { get; } = new();
            internal int Copies { get; private set; }
            internal IGpuTexture? LastDestination { get; private set; }

            public bool SwapchainCopySourceRequested
            {
                get => _requested;
                set
                {
                    _requested = value;
                    Requested.Add(value);
                }
            }

            public bool SwapchainCopySupported { get; set; } = true;

            public bool SwapchainCopyable { get; set; }

            public void CopySwapchainColor(IGpuCommandList commands, IGpuTexture destination)
            {
                if (!SwapchainCopyable) throw new InvalidOperationException("The swapchain image is not copyable.");
                Copies++;
                LastDestination = destination;
            }
        }
    }
}
