using KhaozEngine.Game;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    /// <summary>
    /// The windowed path of the #423 defect, closed by
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/429">#429</see>: the loop's pre-record phase
    /// (<see cref="FramePhases"/>) driving a real <see cref="Scene3D"/> through <see cref="GameApp3D.PrepareScene"/>,
    /// the way <c>GameApp3D.OnPrepareWorld</c> does, followed by the record phase doing what
    /// <c>Render3DSurface.Render</c> does.
    /// <para>
    /// <c>AppWindow</c> and <c>GameApp3D</c> instances both need a real window (a GLFW window, a swapchain and a
    /// GPU device), so neither is constructed here. Everything between them that CAN be driven headless is: the
    /// loop's phase order is production code (<see cref="FramePhases"/>, the whole of what <c>AppWindow.Run</c> does
    /// per frame), and so is the scene ordering (<see cref="GameApp3D.PrepareScene"/>, the whole of what
    /// <c>OnPrepareWorld</c> does after the timing flag). What is left uncovered is the two-line wiring that hands
    /// one to the other, which is compile-checked and covered by the windowed playtest on the issue.
    /// </para>
    /// </summary>
    public sealed class WindowedFramePrepareTests
    {
        const int W = 128, H = 96;

        static IGpuFramebuffer NewTarget(IGpuResourceFactory f)
        {
            IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            return f.CreateFramebuffer(null, tex);
        }

        // The smallest sea the producer will build: these tests are about frame structure, not the surface, and the
        // CPU spectrum bake is the only slow part of it.
        static Scene3D NewOceanScene(IGpuDevice device, IGpuFramebuffer fb)
        {
            var scene = new Scene3D(device, fb.Outputs);
            scene.Post.Starfield = false;
            scene.Post.Water.WaveSource = WaterWaveSource.FftOcean;
            scene.Post.Water.SeaState.CascadeCount = 1;
            scene.Post.Water.SeaState.CascadeResolution = 32;
            return scene;
        }

        static void QueueOcean(Scene3D scene) => scene.DrawWater(new WaterPlane(0f, 0f, 0f, 64f));

        static Frame NewFrame(IGpuCommandList commands) => new()
        {
            Dt = 1f / 60f,
            Width = W,
            Height = H,
            LogicalWidth = W,
            LogicalHeight = H,
            Commands = commands,
        };

        /// <summary>
        /// The whole fix in one frame. The ocean primes during the prepare phase, on a command list of its own, and
        /// by the time the record phase runs the frame's list is the only thing open.
        /// </summary>
        [Fact]
        public void The_ocean_primes_in_the_prepare_phase_and_nothing_nests_in_the_frames_list()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using Scene3D scene = NewOceanScene(device, fb);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            int beginsBeforePrepare = 0, beginsAfterPrepare = 0, openAfterPrepare = -1, openDuringRecord = -1;
            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: _ =>
                {
                    beginsBeforePrepare = device.Begins;
                    GameApp3D.PrepareScene(scene, QueueOcean);
                    beginsAfterPrepare = device.Begins;
                    openAfterPrepare = device.OpenLists;
                },
                onFrame: _ => openDuringRecord = device.OpenLists);

            // The prime really did open (and close) a list of its own, in the prepare phase. Without this the
            // peak-of-1 assertions below would be satisfied by an ocean that never primed at all.
            Assert.True(beginsAfterPrepare > beginsBeforePrepare,
                "the ocean prime is supposed to open its own command list during the prepare phase, and opened none");
            Assert.Equal(0, openAfterPrepare);
            Assert.Equal(1, openDuringRecord);
            Assert.Equal(1, device.PeakOpenLists);
            Assert.Equal(0, device.OpenLists);
        }

        /// <summary>
        /// The idempotency interaction 17.26.0 designed for, now that both call sites are live on one frame:
        /// <c>GameApp3D</c> prepares in the pre-record phase and <c>Render3DSurface.Render</c> prepares again inside
        /// the record phase. The hook's call must be the EFFECTIVE one and the surface's the no-op, not the other
        /// way round, because the surface's runs with the frame's list already open.
        /// </summary>
        [Fact]
        public void The_surfaces_own_prepare_inside_the_record_phase_is_the_no_op()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using Scene3D scene = NewOceanScene(device, fb);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            int beginsAtRecordEntry = 0, beginsAfterSecondPrepare = 0;
            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: _ => GameApp3D.PrepareScene(scene, QueueOcean),
                onFrame: f =>
                {
                    beginsAtRecordEntry = device.Begins;
                    scene.PrepareFrame();          // exactly what Render3DSurface.Render does first
                    beginsAfterSecondPrepare = device.Begins;
                    scene.RenderInternal(f.Commands, W, H, fb);
                });

            Assert.Equal(beginsAtRecordEntry, beginsAfterSecondPrepare);
            Assert.Equal(1, device.PeakOpenLists);
        }

        /// <summary>
        /// The same scene work driven entirely from the record phase, which is what the windowed loop forced before
        /// the phase split existed. It used to reach a peak of two open lists and corrupt the device on Direct3D11
        /// in immediate-context mode. It is now REFUSED at the seam, by name, on every backend and with no GPU
        /// involved: the frame's list holds the device and the ocean's priming pass cannot open a second one
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>).
        /// <para>
        /// This is also what stops the assertions above passing vacuously. A scene that primed nothing would
        /// satisfy every "the peak never reached 2" test in this file, and would not throw here.
        /// </para>
        /// </summary>
        [Fact]
        public void Preparing_from_the_record_phase_is_refused_by_name()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using Scene3D scene = NewOceanScene(device, fb);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            var ex = Assert.Throws<GpuNestedRecordingException>(() =>
                FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                    onPrepare: null,
                    onFrame: _ => GameApp3D.PrepareScene(scene, QueueOcean)));

            Assert.Equal("the window's frame list", ex.Owner);
            Assert.Contains("ocean", ex.Attempted);
            // Both halves of the diagnosis reach the reader, and the message names the fix rather than the symptom.
            Assert.Contains("the window's frame list", ex.Message);
            Assert.Contains("pre-record phase", ex.Message);
            // And the refusal left nothing half-open: the frame's own list still closed on the way out.
            Assert.Equal(0, device.OpenLists);
            Assert.Equal(1, device.PeakOpenLists);
        }

        /// <summary>
        /// The scene ordering <c>OnPrepareWorld</c> relies on: the queue fill runs between <c>Begin</c> and
        /// <c>PrepareFrame</c>. A fill that landed after the prepare would be the misuse the water pass throws for,
        /// and one that landed before the begin would be dropped by it.
        /// </summary>
        [Fact]
        public void PrepareScene_begins_then_queues_then_prepares()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using Scene3D scene = NewOceanScene(device, fb);

            int planesWhenQueued = -1;
            scene.DrawWater(new WaterPlane(0f, 0f, 0f, 8f));      // a stale plane from an earlier frame
            GameApp3D.PrepareScene(scene, s =>
            {
                planesWhenQueued = s.WaterPlaneCount;             // Begin has run, so the stale plane is gone
                QueueOcean(s);
            });

            Assert.Equal(0, planesWhenQueued);
            Assert.Equal(1, scene.WaterPlaneCount);               // and this frame's plane survived the prepare
        }

        /// <summary>
        /// A frame that queues no water pays nothing for the phase, so the split costs a 3D game with no ocean
        /// exactly one delegate call.
        /// </summary>
        [Fact]
        public void A_frame_with_no_water_opens_no_list_in_the_prepare_phase()
        {
            using var device = new OpenListTrackingGpuDevice();
            using IGpuFramebuffer fb = NewTarget(device.Factory);
            using Scene3D scene = NewOceanScene(device, fb);
            using IGpuCommandList frameList = device.Factory.CreateCommandList();

            int beginsAfterPrepare = 0;
            FramePhases.Run(NewFrame(frameList), render: true, device, frameList, Color.Black,
                onPrepare: _ =>
                {
                    GameApp3D.PrepareScene(scene, _ => { });
                    beginsAfterPrepare = device.Begins;
                },
                onFrame: _ => { });

            Assert.Equal(0, beginsAfterPrepare);
            Assert.Equal(1, device.Begins);   // the frame's own list, and nothing else all frame
        }
    }
}
