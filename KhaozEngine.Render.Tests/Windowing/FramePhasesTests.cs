using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing
{
    /// <summary>
    /// The windowed loop's two-phase frame contract (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/429">#429</see>),
    /// checked on <see cref="OpenListTrackingGpuDevice"/> - no GPU, so it runs under a plain <c>dotnet test</c>.
    /// <para>
    /// <c>AppWindow.Run</c> itself needs a real GLFW window and a real swapchain, so it cannot be driven headless
    /// and is not driven here. What IS driven is <see cref="FramePhases"/>, which is the whole of what that loop
    /// does per frame: the loop body latches <c>Frame</c> and hands the two callbacks straight to it. So the
    /// ordering asserted below is the production ordering, not a re-implementation of it in a test.
    /// </para>
    /// <para>
    /// The fact under test is not "a picture is right", it is "nobody opens a second command list while the frame's
    /// is recording". With Direct3D11 in immediate-context mode a command list IS the device's immediate context
    /// and <c>Begin</c> resets it, so a nested open silently invalidates the frame's bindings and the device faults
    /// a few draws later (#423). The tracker counts open lists, which is the cheap headless stand-in for that fault.
    /// </para>
    /// </summary>
    public sealed class FramePhasesTests
    {
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

        [Fact]
        public void Prepare_runs_with_nothing_recording_and_the_frame_runs_inside_the_frame_list()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            int openDuringPrepare = -1, openDuringFrame = -1;
            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: _ => openDuringPrepare = device.OpenLists,
                onFrame: _ => openDuringFrame = device.OpenLists);

            Assert.Equal(0, openDuringPrepare);
            Assert.Equal(1, openDuringFrame);
            Assert.Equal(0, device.OpenLists);       // the frame's list was closed again
            Assert.Equal(1, device.Submits);
        }

        [Fact]
        public void The_two_phases_run_in_order_and_exactly_once_each()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            var order = new System.Collections.Generic.List<string>();
            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: _ => order.Add("prepare"),
                onFrame: _ => order.Add("frame"));

            Assert.Equal(new[] { "prepare", "frame" }, order);
        }

        [Fact]
        public void Both_callbacks_get_the_frame_the_loop_latched()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            Frame frame = NewFrame(frameList);

            Frame? seenByPrepare = null, seenByFrame = null;
            FramePhases.Run(frame, render: true, device, frameList, Color.Black,
                onPrepare: f => seenByPrepare = f,
                onFrame: f => seenByFrame = f);

            // The prepare phase sees THIS frame's dt / input / size, which is what lets a host fill the frame's
            // queues there rather than a frame late.
            Assert.Same(frame, seenByPrepare);
            Assert.Same(frame, seenByFrame);
        }

        /// <summary>
        /// The point of the phase: a callback may open, submit and drain a command list of its own there, and the
        /// frame's list is still closed, so nothing nests. This is exactly what the FFT ocean's priming pass does.
        /// </summary>
        [Fact]
        public void A_prepare_callback_may_open_its_own_command_list_without_nesting()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            using IGpuCommandList ownList = device.Factory.CreateCommandList();

            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: _ =>
                {
                    ownList.Begin();
                    ownList.End();
                    device.Submit(ownList);
                    device.WaitForIdle();
                },
                onFrame: _ => { });

            Assert.Equal(2, device.Begins);          // the producer's list and the frame's
            Assert.Equal(1, device.PeakOpenLists);   // but never both at once
        }

        /// <summary>
        /// The same work from the RECORD phase is the shape this issue removed, and the tracker must see it. Without
        /// this, every "the peak never reached 2" assertion above would also pass on a tracker that counted nothing.
        /// </summary>
        [Fact]
        public void The_same_work_from_the_record_phase_is_the_nesting_this_replaced()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();
            using IGpuCommandList ownList = device.Factory.CreateCommandList();

            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: null,
                onFrame: _ => { ownList.Begin(); ownList.End(); });

            Assert.Equal(2, device.PeakOpenLists);
        }

        [Fact]
        public void A_null_prepare_is_the_single_callback_loop()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            int openDuringFrame = -1;
            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: null,
                onFrame: _ => openDuringFrame = device.OpenLists);

            Assert.Equal(1, openDuringFrame);
            Assert.Equal(1, device.Begins);
            Assert.Equal(1, device.Submits);
        }

        /// <summary>
        /// A minimized window opens and presents nothing, but both phases still run so simulation, netcode and
        /// timers keep advancing. That was true of the single callback before the split and stays true of both.
        /// </summary>
        [Fact]
        public void A_render_suppressed_frame_opens_no_list_and_still_runs_both_phases()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            bool prepared = false, framed = false;
            FramePhases.Run(NewFrame(frameList, renderSuppressed: true), render: false, device, frameList, Color.Black,
                onPrepare: _ => prepared = true,
                onFrame: _ => framed = true);

            Assert.True(prepared);
            Assert.True(framed);
            Assert.Equal(0, device.Begins);
            Assert.Equal(0, device.Submits);
        }
    }
}
