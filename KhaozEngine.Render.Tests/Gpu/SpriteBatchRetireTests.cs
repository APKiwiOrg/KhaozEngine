using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The 2D batch's mid-life disposals, driven device-free. <c>EvictStaleSets</c> used to call a full
    /// <c>WaitForIdle</c> from <c>NewFrame</c> the moment any (texture,sampler) set crossed the eviction cutoff, so
    /// a game that streams sprites bought a whole-pipeline stall on the frame thread roughly every ten seconds
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/84">#84</see>). It feeds the seam's
    /// <see cref="GpuRetireQueue"/> now, so what these pin is that eviction costs no drain at all AND that the set
    /// still outlives the frames that could reference it.
    /// </summary>
    public sealed class SpriteBatchRetireTests
    {
        const int W = 32, H = 32;
        static readonly Color White = new(1, 1, 1, 1);

        sealed class Rig
        {
            internal required SpyGpuDevice Device { get; init; }
            internal required FakeGpuResourceFactory Factory { get; init; }
            internal required SpriteBatch Batch { get; init; }
            internal required IGpuCommandList Commands { get; init; }

            internal Texture2D NewTexture()
                => new(Device, Device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                    1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled)), 1, 1, ownsHandle: true);

            /// <summary>One frame: the boundary, then a pass that draws each texture once.</summary>
            internal void Frame(params Texture2D[] textures)
            {
                Batch.NewFrame(Commands, W, H);
                Batch.Begin();
                foreach (Texture2D t in textures) Batch.Draw(t, Vector2.Zero, White);
                Batch.End();
            }
        }

        static Rig NewRig(int evictAfterFrames)
        {
            var inner = new FakeGpuDevice();
            var device = new SpyGpuDevice(inner);
            IGpuTexture target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = device.Factory.CreateFramebuffer(null, target);
            var batch = new SpriteBatch(device, fb.Outputs) { SetEvictAfterFrames = evictAfterFrames };
            return new Rig
            {
                Device = device,
                Factory = (FakeGpuResourceFactory)inner.Factory,
                Batch = batch,
                Commands = device.Factory.CreateCommandList(),
            };
        }

        [Fact]
        public void Crossing_the_eviction_cutoff_never_drains_the_device()
        {
            Rig rig = NewRig(evictAfterFrames: 2);
            Texture2D a = rig.NewTexture();
            Texture2D b = rig.NewTexture();

            rig.Frame(a, b);
            Assert.Equal(2, rig.Batch.CachedSetCount);

            // Drop a out of the working set. Its set is evicted on the third frame, and every frame after that is a
            // frame the old code would have drained on had another texture aged out.
            for (int i = 0; i < 40; i++) rig.Frame(b);

            Assert.Equal(1, rig.Batch.CachedSetCount);      // a's set really did leave the cache
            Assert.Equal(0, rig.Device.WaitForIdleCalls);   // and cost the frame thread nothing to do it
        }

        [Fact]
        public void An_evicted_set_is_destroyed_only_after_the_deferral_window()
        {
            // Leaving the cache and being destroyed are two different moments now. The gap is what keeps a queued
            // draw that still names the set from reading a freed binding, and it is the whole reason the drain the
            // eviction path used to take could be removed rather than just moved.
            Rig rig = NewRig(evictAfterFrames: 2);
            Texture2D a = rig.NewTexture();
            Texture2D b = rig.NewTexture();

            rig.Frame(a, b);
            rig.Frame(b);
            rig.Frame(b);          // a is now 2 frames stale: evicted here

            Assert.Equal(1, rig.Batch.CachedSetCount);
            Assert.Equal(0, rig.Factory.DisposedResourceSetCount);   // out of the cache, NOT yet freed

            for (int i = 0; i < rig.Batch.VertexBufferRingDepth; i++)
            {
                rig.Frame(b);
                Assert.Equal(0, rig.Factory.DisposedResourceSetCount);   // still inside the window
            }

            rig.Frame(b);   // the boundary that closes the window

            Assert.Equal(1, rig.Factory.DisposedResourceSetCount);
            Assert.Equal(0, rig.Device.WaitForIdleCalls);
        }

        [Fact]
        public void A_returning_texture_rebuilds_its_set_and_the_old_one_is_still_freed()
        {
            Rig rig = NewRig(evictAfterFrames: 2);
            Texture2D a = rig.NewTexture();
            Texture2D b = rig.NewTexture();

            rig.Frame(a, b);
            for (int i = 0; i < 3; i++) rig.Frame(b);   // a evicted, its set retired and not yet freed
            rig.Frame(a, b);                            // a returns: a NEW set is built for it

            Assert.Equal(2, rig.Batch.CachedSetCount);

            for (int i = 0; i < 8; i++) rig.Frame(a, b);

            // Exactly the retired one died. The rebuilt set is live and in the cache, so freeing it here would be
            // the use-after-free the deferral exists to prevent.
            Assert.Equal(1, rig.Factory.DisposedResourceSetCount);
            Assert.Equal(2, rig.Batch.CachedSetCount);
            Assert.Equal(0, rig.Device.WaitForIdleCalls);
        }

        [Fact]
        public void The_frame_boundary_opens_no_recording_of_its_own()
        {
            // WHY THE QUEUE IS FRAME-COUNTED RATHER THAN FENCED, pinned as a test because the alternative looks
            // like a free upgrade in a diff. NewFrame runs INSIDE the frame's own command-list recording (GameApp's
            // record phase, and every offscreen capture host does the same), and minting a retirement fence means
            // opening a second command list on the device, which the seam refuses by name (#424). Swapping this
            // batch's GpuRetireQueue.CreateFrameCounted for GpuRetireQueue.Create fails right here.
            Rig rig = NewRig(evictAfterFrames: 2);
            Texture2D a = rig.NewTexture();
            Texture2D b = rig.NewTexture();

            using (GpuRecording.Open(rig.Device, rig.Commands, "the window's frame list"))
            {
                rig.Frame(a, b);
                for (int i = 0; i < 20; i++) rig.Frame(b);
            }

            Assert.Equal(1, rig.Batch.CachedSetCount);
            Assert.Equal(1, rig.Factory.DisposedResourceSetCount);
            Assert.Equal(0, rig.Device.WaitForIdleCalls);
        }

        [Fact]
        public void Dispose_frees_the_retired_tail_behind_one_drain()
        {
            // The tail would otherwise outlive the batch. Teardown is the one place the drain is kept, because a
            // stall costs nothing there and there is no later boundary to wait for.
            Rig rig = NewRig(evictAfterFrames: 2);
            Texture2D a = rig.NewTexture();
            Texture2D b = rig.NewTexture();

            rig.Frame(a, b);
            rig.Frame(b);
            rig.Frame(b);   // a's set retired, still inside the deferral window
            Assert.Equal(0, rig.Factory.DisposedResourceSetCount);

            rig.Batch.Dispose();

            Assert.Equal(1, rig.Device.WaitForIdleCalls);            // exactly one, and it is at teardown
            Assert.Equal(rig.Factory.ResourceSets.Count, rig.Factory.DisposedResourceSetCount);   // nothing leaked
        }
    }
}
