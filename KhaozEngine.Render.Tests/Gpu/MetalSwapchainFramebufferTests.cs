using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE SWAPCHAIN's FRAMEBUFFER: M-W7's stable object identity, and the moving attachment-set <c>Id</c> row
    /// 12's correction requires. Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581).
    ///
    /// <para><b>THE TWO CLAIMS ARE DIFFERENT AND THE WHOLE OF ROW 12's CORRECTION IS THAT THEY ARE.</b> The OBJECT
    /// never changes, so anything holding <c>IGpuDevice.SwapchainFramebuffer</c> may cache it across every resize
    /// and every acquire. The <see cref="MetalBoundFramebuffer.Id"/> changes on every acquire, because M-A6's
    /// guard is <c>if (framebuffer.Id == _framebuffer.Id) return;</c> and it returns BEFORE copying the incoming
    /// record: a source whose texture moves under a stable number leaves the schedule describing the drawable the
    /// present has already moved past, and nothing anywhere reports it.</para>
    /// </summary>
    public sealed class MetalSwapchainFramebufferTests
    {
        /// <summary>What it publishes at construction, and the shape that never moves after it.</summary>
        [Fact]
        public void ItPublishesTheFirstAttachmentAndFixesItsOutputs()
        {
            MetalSwapchainFramebuffer fb = Make(new IntPtr(0x10), 1280u, 720u);

            Assert.Equal(1280u, fb.Width);
            Assert.Equal(720u, fb.Height);
            Assert.Equal(new IntPtr(0x10), fb.Attachment.Texture);
            Assert.Equal(1uL, fb.Generation);

            // NO DEPTH AND NO MSAA, both matching the incumbent as the engine drives it: the one windowed site in
            // GpuDeviceContext passes a null depth format, and GpuWindowedDeviceRequest has no field for one.
            Assert.Null(fb.Outputs.Depth);
            Assert.Equal(1, fb.Outputs.SampleCount);
            Assert.Equal(GpuPixelFormat.B8G8R8A8UNorm, fb.Outputs.Colour[0]);
        }

        /// <summary>
        /// EVERY PUBLISH MINTS A FRESH <c>Id</c>, WHICH IS THE CORRECTION ACTED ON. A stable one is what an
        /// ordinary <see cref="MetalFramebuffer"/> keeps forever and is right there because its handles never
        /// move.
        /// </summary>
        [Fact]
        public void EveryPublishMintsAFreshAttachmentSetId()
        {
            MetalSwapchainFramebuffer fb = Make(new IntPtr(0x10), 8u, 8u);
            ulong first = Bound(fb).Id;

            fb.Adopt(new MetalAttachment(new IntPtr(0x20), GpuPixelFormat.B8G8R8A8UNorm),
                new MetalDrawableSize(8u, 8u));
            ulong second = Bound(fb).Id;

            Assert.NotEqual(first, second);
            Assert.Equal(new IntPtr(0x20), Bound(fb).Colour![0].Texture);
            Assert.Equal(2uL, fb.Generation);
        }

        /// <summary>
        /// AND THE NUMBER IS NEVER ZERO AND NEVER COLLIDES WITH AN ORDINARY FRAMEBUFFER's. Zero means "nothing
        /// bound" to the schedule, which is what a fresh recording holds, so a swapchain framebuffer that
        /// published a zero would be invisible to the framebuffer-change guard entirely.
        /// </summary>
        [Fact]
        public void TheIdIsNeverZeroAndSitsInItsOwnHalfOfTheSpace()
        {
            MetalSwapchainFramebuffer fb = Make(new IntPtr(0x10), 8u, 8u);

            for (int i = 0; i < 4; i++)
            {
                Assert.NotEqual(0uL, Bound(fb).Id);
                Assert.True(Bound(fb).IsBound);

                // The counter starts in the negative half of the signed space, which no ordinary framebuffer
                // counter reaches, so the two can never answer the same number.
                Assert.True(Bound(fb).Id > long.MaxValue);

                fb.Adopt(new MetalAttachment(new IntPtr(0x20 + i), GpuPixelFormat.B8G8R8A8UNorm),
                    new MetalDrawableSize(8u, 8u));
            }
        }

        /// <summary>
        /// A RESIZE MOVES THE SIZE AND NOT THE OBJECT, which is M-W7. On this API it is free rather than built:
        /// there is no per-image view object for a resize to invalidate, which is what W2 asked Direct3D 11 for
        /// and what V-W5 had to construct for Vulkan.
        /// </summary>
        [Fact]
        public void AResizeMovesTheSizeUnderTheSameObject()
        {
            MetalSwapchainFramebuffer fb = Make(new IntPtr(0x10), 1280u, 720u);

            fb.Adopt(new MetalAttachment(new IntPtr(0x20), GpuPixelFormat.B8G8R8A8UNorm),
                new MetalDrawableSize(640u, 480u));

            Assert.Equal(640u, fb.Width);
            Assert.Equal(480u, fb.Height);
            Assert.Equal(640u, Bound(fb).Width);
            Assert.Equal(480u, Bound(fb).Height);
        }

        /// <summary>
        /// A ZERO ATTACHMENT IS REFUSED BY NAME. A swapchain framebuffer always has a colour target: a nil drawable
        /// binds the ORPHAN target rather than nothing (M-W5), so a zero handle reaching here means the orphan was
        /// published before it had been created, and binding it would rasterise into a pass Metal reports as
        /// having no attachments rather than as a wrong argument.
        /// </summary>
        [Fact]
        public void AZeroAttachmentIsRefusedRatherThanPublished()
        {
            MetalSwapchainFramebuffer fb = Make(new IntPtr(0x10), 8u, 8u);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => fb.Adopt(
                new MetalAttachment(IntPtr.Zero, GpuPixelFormat.B8G8R8A8UNorm), new MetalDrawableSize(8u, 8u)));

            Assert.Contains("orphan target", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// DISPOSING IT FROM OUTSIDE IS A NO-OP AND THE WRAPPER KEEPS WORKING, matching the incumbent's no-dispose
        /// wrapper over a device-owned swapchain framebuffer. It owns nothing: the colour attachment is the
        /// drawable's texture or the device's orphan target, and neither is this object's to release.
        /// </summary>
        [Fact]
        public void DisposingItFromOutsideBreaksNothing()
        {
            MetalSwapchainFramebuffer fb = Make(new IntPtr(0x10), 8u, 8u);

            ((IGpuFramebuffer)fb).Dispose();

            Assert.True(fb.IsDisposed);
            fb.Adopt(new MetalAttachment(new IntPtr(0x20), GpuPixelFormat.B8G8R8A8UNorm),
                new MetalDrawableSize(8u, 8u));
            Assert.Equal(new IntPtr(0x20), fb.Attachment.Texture);
        }

        /// <summary>
        /// THE DISCRIMINATOR ROW 12 ADDED, PINNED FROM BOTH SIDES. <c>IsSwapchain</c> is what tells the swapchain's
        /// own framebuffer from an aggregate over engine textures.
        /// <para>
        /// <b>ROW 15 FOUND IT HAS NO PRODUCTION READER ON THIS BACKEND, and the reason is M-W6.</b> The row 12
        /// handoff described it as what the present path asks before deciding whether a frame has anything to
        /// present. The present path does not ask it: <c>-presentDrawable:</c> presents a DRAWABLE rather than a
        /// framebuffer, and the answer to "is there anything to present" is whether a drawable is held. The Vulkan
        /// sibling's reader is its semaphore routing (which submit carries the acquire semaphore), and M-W6's
        /// separate present buffer plus queue-order execution removes that question entirely here. It is kept as
        /// the discriminator both sources answer, and this is what stops either answer drifting.
        /// </para>
        /// </summary>
        [Fact]
        public void TheSwapchainFramebufferIsTheOnlySourceThatSaysItIsOne()
        {
            MetalSwapchainFramebuffer swapchain = Make(new IntPtr(0x10), 8u, 8u);

            Assert.True(((IMetalBoundFramebufferSource)swapchain).IsSwapchain);
        }

        /// <summary>
        /// THE CORRECTION, DRIVEN THROUGH THE REAL SCHEDULE RATHER THAN ASSERTED ABOUT A NUMBER. This is the row
        /// that would have failed on the shape row 12's handoff originally described: bind, draw, publish a new
        /// texture, bind again, draw again, and read which texture the second pass actually named.
        /// <para>
        /// WITH A STABLE Id THE SECOND PASS NAMES THE FIRST DRAWABLE. <see cref="MetalRenderPassSchedule.SetFramebuffer"/>
        /// returns on a matching Id before copying the incoming record, so the <c>AsBound</c> read still runs and
        /// its result is dropped, and the frame renders into a drawable the present has moved past.
        /// </para>
        /// </summary>
        [Fact]
        public void AMovedTextureReachesTheScheduleBecauseTheIdMovedWithIt()
        {
            var encoders = new FakeMetalEncoderCalls();
            var render = new FakeMetalRenderCalls();
            var scope = new MetalEncoderScope(new FakeMetalEncoderSink(encoders));
            scope.BeginRecording(new IntPtr(0x100));
            var schedule = new MetalRenderPassSchedule(scope, new FakeMetalRenderApi(render));

            MetalSwapchainFramebuffer fb = Make(new IntPtr(0x10), 8u, 8u);
            IMetalBoundFramebufferSource source = fb;

            MetalBoundFramebuffer first = source.AsBound;
            schedule.SetFramebuffer(in first);
            schedule.PrepareDraw();

            Assert.Equal(new IntPtr(0x10), render.Passes[0].Colour[0].Texture);

            // AN ACQUIRE MOVED THE TEXTURE. The boundary does this on the submit thread with no recording in
            // flight, and this row does it mid-recording precisely because that is the case the Id has to survive.
            fb.Adopt(new MetalAttachment(new IntPtr(0x20), GpuPixelFormat.B8G8R8A8UNorm),
                new MetalDrawableSize(8u, 8u));

            MetalBoundFramebuffer second = source.AsBound;
            schedule.SetFramebuffer(in second);
            schedule.PrepareDraw();

            Assert.Equal(2, render.Passes.Count);
            Assert.Equal(new IntPtr(0x20), render.Passes[1].Colour[0].Texture);
        }

        /// <summary>
        /// AND THE OTHER HALF OF M-A6 STILL HOLDS: a rebind with NO acquire between it and the last one is a
        /// no-op, so the guard is not simply defeated. Two <c>SetFramebuffer</c> calls in one recording always see
        /// the same number, because an acquire only ever happens at a present boundary and a boundary starts a new
        /// recording. That is why minting a fresh Id per acquire costs nothing at all: the re-emitted viewport and
        /// scissor row 12's handoff weighed as its price never arrive.
        /// </summary>
        [Fact]
        public void ARebindWithNoAcquireBetweenIsStillANoOp()
        {
            var encoders = new FakeMetalEncoderCalls();
            var render = new FakeMetalRenderCalls();
            var scope = new MetalEncoderScope(new FakeMetalEncoderSink(encoders));
            scope.BeginRecording(new IntPtr(0x100));
            var schedule = new MetalRenderPassSchedule(scope, new FakeMetalRenderApi(render));

            IMetalBoundFramebufferSource source = Make(new IntPtr(0x10), 8u, 8u);

            MetalBoundFramebuffer bound = source.AsBound;
            schedule.SetFramebuffer(in bound);
            schedule.PrepareDraw();

            int viewportsAfterFirst = render.Viewports.Count;
            int passesAfterFirst = render.Passes.Count;

            MetalBoundFramebuffer again = source.AsBound;
            schedule.SetFramebuffer(in again);

            Assert.Equal(viewportsAfterFirst, render.Viewports.Count);
            Assert.Equal(passesAfterFirst, render.Passes.Count);
        }

        static MetalSwapchainFramebuffer Make(IntPtr texture, uint width, uint height)
            => new(GpuPixelFormat.B8G8R8A8UNorm,
                new MetalAttachment(texture, GpuPixelFormat.B8G8R8A8UNorm),
                new MetalDrawableSize(width, height));

        static MetalBoundFramebuffer Bound(MetalSwapchainFramebuffer fb)
            => ((IMetalBoundFramebufferSource)fb).AsBound;
    }
}
