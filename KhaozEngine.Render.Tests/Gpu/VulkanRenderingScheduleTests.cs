using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEFERRED BEGIN, DRIVEN WITH NO DEVICE (V-A1 to V-A6, section 7). Work-breakdown row 12
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522).
    ///
    /// <para><b>EVERYTHING THIS ROW CAN GET WRONG IS ABOVE THE NATIVE LINE, WHICH IS WHY IT IS ALL HERE.</b>
    /// <see cref="VulkanRenderApi"/> makes six <c>vkCmd*</c> calls and takes no decision at all: whether to begin,
    /// what each attachment's load op is, whether a clear folds or is issued, whether the pass must close, and
    /// whether a framebuffer bind owes a viewport all live in <see cref="VulkanRenderingSchedule"/>, and
    /// <see cref="FakeVulkanRenderApi"/> is that seam with the arguments kept.</para>
    ///
    /// <para><b>THE ONE ASSERTION THAT MATTERS MOST DOES NOT THROW WHEN IT FAILS.</b> A positive viewport height
    /// renders every golden upside down and nothing anywhere reports an error, which is why V-A5 asserts it three
    /// ways: by the capability parity test (row 18), by all 36 goldens, and by
    /// <see cref="TheEmittedViewport_HasANegativeHeightAndTheShiftedOrigin"/> here, on the free device-free leg,
    /// which is the only one of the three that fails before a GPU is involved.</para>
    ///
    /// <para><b>THE COUNTS HERE ARE THE VIEWPORT AND SCISSOR HALF OF MEASUREMENT GATE MV4</b>, which row 11's
    /// budget file deferred to this row because neither call is a member of <see cref="IVkCmdSink"/> and no
    /// counting seam over that budget can see them. See
    /// <c>VulkanBindBudgetTests.TheSeam_CannotSeeTheViewportHalfOfTheGate</c> for the split and why the two seams
    /// stay separate.</para>
    /// </summary>
    public sealed class VulkanRenderingScheduleTests
    {
        const ulong Buffer = 0xC0FFEE;

        // ---- The deferred begin and the clear folding (V-A2) ----

        /// <summary>
        /// A CLEAR RECORDED BEFORE THE FIRST DRAW FOLDS INTO <c>loadOp = CLEAR</c> AND COSTS NO CALL, which is the
        /// whole reason the begin is deferred. The incumbent takes a <c>vkCmdClearAttachments</c> for the same
        /// sequence, because its begin is not deferred and the clear therefore always arrives inside an open pass.
        /// </summary>
        [Fact]
        public void AClearBeforeTheFirstDraw_FoldsIntoLoadOpAndCostsNoClearCall()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true));
            schedule.ClearColourTarget(Buffer, 0, new Color(0.25f, 0.5f, 0.75f, 1f));
            schedule.ClearDepthStencil(Buffer, 0.5f);
            schedule.PrepareDraw(Buffer);

            VulkanRecordedBegin begin = Assert.Single(api.Begins);
            Assert.Empty(api.Clears);

            Assert.Equal(VulkanLoadOp.Clear, begin.Colour[0].LoadOp);
            Assert.Equal(new Color(0.25f, 0.5f, 0.75f, 1f), begin.Colour[0].ClearValue);

            VulkanDepthAttachment depth = Assert.NotNull(begin.Depth);
            Assert.Equal(VulkanLoadOp.Clear, depth.LoadOp);
            Assert.Equal(0.5f, depth.ClearDepth);
        }

        /// <summary>
        /// AND AN ATTACHMENT WITH NO PENDING CLEAR LOADS RATHER THAN CLEARING, which is the other half of the same
        /// rule and the half that quietly destroys a pass's inputs when it is wrong. A post chain reads what the
        /// previous pass wrote, so an attachment clearing because its neighbour did would blank it.
        /// </summary>
        [Fact]
        public void AnAttachmentWithNoPendingClear_LoadsRatherThanClears()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 3, depth: true));
            schedule.ClearColourTarget(Buffer, 1, Color.White);
            schedule.PrepareDraw(Buffer);

            VulkanRecordedBegin begin = Assert.Single(api.Begins);

            Assert.Equal(VulkanLoadOp.Load, begin.Colour[0].LoadOp);
            Assert.Equal(VulkanLoadOp.Clear, begin.Colour[1].LoadOp);
            Assert.Equal(VulkanLoadOp.Load, begin.Colour[2].LoadOp);
            Assert.Equal(VulkanLoadOp.Load, Assert.NotNull(begin.Depth).LoadOp);
        }

        /// <summary>
        /// A CLEAR THAT ARRIVES AFTER THE PASS HAS OPENED IS A <c>vkCmdClearAttachments</c>, which is what the
        /// incumbent does in the same situation and is the arm the deferral does not remove. The begin that
        /// preceded it must still read <c>LOAD</c>: a fold applied retroactively would clear the draw that had
        /// already gone out.
        /// </summary>
        [Fact]
        public void AClearAfterTheFirstDraw_IsAClearAttachmentsCall()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true));
            schedule.PrepareDraw(Buffer);
            schedule.ClearColourTarget(Buffer, 0, Color.Black);
            schedule.ClearDepthStencil(Buffer, 1f);

            Assert.Equal(VulkanLoadOp.Load, Assert.Single(api.Begins).Colour[0].LoadOp);
            Assert.Equal(2, api.Clears.Count);
            Assert.False(api.Clears[0].Depth);
            Assert.True(api.Clears[1].Depth);
            Assert.Equal(1f, api.Clears[1].DepthValue);
        }

        /// <summary>
        /// <c>storeOp</c> IS NOT EXPRESSIBLE AND <c>DONT_CARE</c> IS NOT EITHER (V-A6). The store is
        /// unconditionally <c>STORE</c>, so the seam offers no way to ask for anything else, and the load enum has
        /// exactly two arms. Undefined contents are not stable across runs and the goldens require stability on
        /// the same rasterizer, so an optimisation here needs its own change with its own determinism argument
        /// rather than an arm somebody can reach by accident.
        /// </summary>
        [Fact]
        public void TheLoadOpEnum_HasNoDontCareArm()
        {
            Assert.Equal(2, Enum.GetValues<VulkanLoadOp>().Length);
            Assert.DoesNotContain("DontCare", Enum.GetNames<VulkanLoadOp>(), StringComparer.Ordinal);
        }

        // ---- The clear-only pass (V-A3) ----

        /// <summary>
        /// THE CLEAR-ONLY PASS STILL CLEARS, THROUGH A BEGIN AND END PAIR WITH NO DRAWS. <c>SetFramebuffer</c>
        /// plus a clear plus <c>End</c> is a shape the incumbent forces at two sites and a golden depends on, and
        /// it is the ONE place a deferred begin needs an explicit flush rather than falling out of the schedule.
        /// </summary>
        [Fact]
        public void AClearOnlyPass_StillClearsThroughABeginAndEndPair()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: false));
            schedule.ClearColourTarget(Buffer, 0, Color.White);
            schedule.EndRendering(Buffer);

            Assert.Equal(VulkanLoadOp.Clear, Assert.Single(api.Begins).Colour[0].LoadOp);
            Assert.Equal(1, api.EndCount);
            Assert.Empty(api.Clears);
        }

        /// <summary>
        /// AND IT FLUSHES AT THE NEXT FRAMEBUFFER CHANGE TOO, not only at <c>End</c>. A pass abandoned by a bind
        /// of a different target has exactly the same clears owed, and a flush that only ran at <c>End</c> would
        /// drop them silently.
        /// </summary>
        [Fact]
        public void AClearOnlyPass_FlushesAtTheNextFramebufferChange()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: false));
            schedule.ClearColourTarget(Buffer, 0, Color.White);
            schedule.SetFramebuffer(Buffer, Framebuffer(2, colour: 1, depth: false));

            Assert.Equal(VulkanLoadOp.Clear, Assert.Single(api.Begins).Colour[0].LoadOp);
            Assert.Equal(1, api.EndCount);
        }

        /// <summary>
        /// AND A REDUNDANT REBIND LEAVES THE CLEAR PENDING RATHER THAN FLUSHING IT, so the next begin still folds
        /// it into <c>loadOp</c>. This is the whole-method identity guard doing work the viewport rule does not
        /// explain: a guard narrowed to the viewport and the scissor would run the rest of the body on a rebind,
        /// which ends the pass, and ending it with clears outstanding spends a begin-and-end pair on them and
        /// leaves the following draw loading instead of clearing. The ordering is correct today and nothing else
        /// pins it, so a future narrowing of that guard has to fail here.
        /// </summary>
        [Fact]
        public void ARedundantRebind_LeavesAPendingClearForTheNextBeginToFold()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);
            VulkanBoundFramebuffer framebuffer = Framebuffer(1, colour: 1, depth: false);

            schedule.SetFramebuffer(Buffer, framebuffer);
            schedule.ClearColourTarget(Buffer, 0, Color.White);
            schedule.SetFramebuffer(Buffer, framebuffer);      // redundant

            Assert.True(schedule.HasPendingClears);
            Assert.Empty(api.Begins);
            Assert.Equal(0, api.EndCount);

            schedule.PrepareDraw(Buffer);

            VulkanRecordedBegin begin = Assert.Single(api.Begins);
            Assert.Equal(VulkanLoadOp.Clear, begin.Colour[0].LoadOp);
            Assert.Equal(Color.White, begin.Colour[0].ClearValue);
            Assert.Empty(api.Clears);
        }

        /// <summary>
        /// TWO CLEARS OF ONE ATTACHMENT BEFORE THE BEGIN FOLD LAST-WINS INTO THE SINGLE <c>loadOp</c> VALUE. A
        /// <c>loadOp</c> carries ONE value per attachment, so the pending slot is a value rather than a queue and
        /// the second write overwrites the first. Emitting the first as a <c>vkCmdClearAttachments</c> to make
        /// room for the second would be a call bought for a colour no draw ever saw, and folding the FIRST would
        /// clear to a value the caller replaced. The depth arm is the same single slot.
        /// </summary>
        [Fact]
        public void TwoClearsOfOneAttachmentBeforeTheBegin_FoldLastWins()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true));
            schedule.ClearColourTarget(Buffer, 0, Color.White);
            schedule.ClearColourTarget(Buffer, 0, Color.Black);
            schedule.ClearDepthStencil(Buffer, 1f);
            schedule.ClearDepthStencil(Buffer, 0.25f);
            schedule.PrepareDraw(Buffer);

            VulkanRecordedBegin begin = Assert.Single(api.Begins);

            Assert.Equal(VulkanLoadOp.Clear, begin.Colour[0].LoadOp);
            Assert.Equal(Color.Black, begin.Colour[0].ClearValue);
            Assert.Equal(0.25f, Assert.NotNull(begin.Depth).ClearDepth);
            Assert.Empty(api.Clears);
        }

        /// <summary>
        /// A PASS WITH NEITHER CLEARS NOR DRAWS OPENS NOTHING, which is what stops the flush above from becoming
        /// a begin-and-end pair per framebuffer bind. A bind is not a pass.
        /// </summary>
        [Fact]
        public void APassWithNoClearsAndNoDraws_OpensNothing()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true));
            schedule.EndRendering(Buffer);

            Assert.Empty(api.Begins);
            Assert.Equal(0, api.EndCount);
        }

        // ---- The end-before-illegal-command invariant (V-A4) ----

        /// <summary>
        /// THE ONE HELPER EVERY COMMAND ILLEGAL INSIDE A PASS CALLS closes an open instance, and calling it again
        /// with nothing open does nothing. That idempotence is what lets a dispatch, a resolve, a copy and a mip
        /// generation each call it unconditionally rather than asking first, which is what "one invariant, one
        /// helper" buys.
        /// </summary>
        [Fact]
        public void EndingBeforeAnIllegalCommand_ClosesAnOpenPassAndIsIdempotent()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true));
            schedule.PrepareDraw(Buffer);
            Assert.True(schedule.IsRendering);

            schedule.EndRendering(Buffer);
            schedule.EndRendering(Buffer);
            schedule.EndRendering(Buffer);

            Assert.False(schedule.IsRendering);
            Assert.Equal(1, api.EndCount);
            Assert.Single(api.Begins);
        }

        /// <summary>
        /// AND A SECOND DRAW IN ONE PASS OPENS NOTHING FURTHER. The instance is per PASS, not per draw, and a
        /// begin per draw would be the render-pass thrash the incumbent's <c>UpdateBuffer</c> split is famous for.
        /// </summary>
        [Fact]
        public void TwoDrawsInOnePass_OpenOneInstance()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true));
            schedule.PrepareDraw(Buffer);
            schedule.PrepareDraw(Buffer);
            schedule.PrepareDraw(Buffer);

            Assert.Single(api.Begins);
            Assert.Single(api.Viewports);
            Assert.Single(api.Scissors);
        }

        // ---- The viewport, and the single most consequential line in the design (V-A5) ----

        /// <summary>
        /// THE EMITTED VIEWPORT HEIGHT IS NEGATIVE AND ITS ORIGIN IS SHIFTED DOWN BY THE HEIGHT. That pair is what
        /// makes Vulkan's clip space match Direct3D's, which is why the incumbent reports
        /// <c>ClipSpaceYInverted = false</c> and why <c>GpuClip.Correct</c> is the identity on this backend: every
        /// matrix the engine builds assumes the flip already happened here.
        /// <para>
        /// GETTING IT WRONG DOES NOT THROW AND DOES NOT FAIL TO RENDER. It renders every golden upside down, which
        /// is why this is asserted on the device-free leg as well as by the goldens and the capability parity
        /// test.
        /// </para>
        /// </summary>
        [Fact]
        public void TheEmittedViewport_HasANegativeHeightAndTheShiftedOrigin()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true, width: 320, height: 240));
            schedule.PrepareDraw(Buffer);

            VulkanViewportRect viewport = Assert.Single(api.Viewports);

            Assert.Equal(-240f, viewport.Height);
            Assert.Equal(240f, viewport.Y);
            Assert.Equal(0f, viewport.X);
            Assert.Equal(320f, viewport.Width);
            Assert.Equal(0f, viewport.MinDepth);
            Assert.Equal(1f, viewport.MaxDepth);
        }

        /// <summary>
        /// AND THE SCISSOR IS NOT FLIPPED WITH IT, which is the mistake the viewport rule invites. A scissor is a
        /// framebuffer-space rectangle with no clip space to correct for, so a negative height there would be a
        /// rectangle the driver rejects rather than an upside-down image.
        /// </summary>
        [Fact]
        public void TheEmittedScissor_IsTheFullFramebufferAndIsNotFlipped()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true, width: 320, height: 240));
            schedule.PrepareDraw(Buffer);

            Assert.Equal(new VulkanScissorRect(0, 0, 320, 240), Assert.Single(api.Scissors));
        }

        /// <summary>
        /// EXACTLY ONE <c>vkCmdSetViewport</c> AND ONE <c>vkCmdSetScissor</c> PER FRAMEBUFFER CHANGE, AND ZERO FOR
        /// A REDUNDANT REBIND. This is measurement gate MV4's viewport half, deferred here by row 11's budget file
        /// because neither call is a member of the sink that budget counts through.
        /// </summary>
        [Fact]
        public void ViewportAndScissor_FollowFramebufferChangesOnly()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            VulkanBoundFramebuffer first = Framebuffer(1, colour: 1, depth: true);
            VulkanBoundFramebuffer second = Framebuffer(2, colour: 1, depth: true);

            schedule.SetFramebuffer(Buffer, first);
            schedule.PrepareDraw(Buffer);
            schedule.SetFramebuffer(Buffer, first);      // redundant
            schedule.PrepareDraw(Buffer);
            schedule.SetFramebuffer(Buffer, first);      // redundant
            schedule.PrepareDraw(Buffer);
            schedule.SetFramebuffer(Buffer, second);     // a change
            schedule.PrepareDraw(Buffer);
            schedule.SetFramebuffer(Buffer, first);      // a change back
            schedule.PrepareDraw(Buffer);

            Assert.Equal(3, api.Viewports.Count);
            Assert.Equal(3, api.Scissors.Count);
            Assert.Equal(3, api.Begins.Count);
        }

        /// <summary>
        /// AND THE REGRESSION THAT COUNT EXISTS FOR: A REDUNDANT REBIND DOES NOT RESTORE THE FULL SCISSOR OVER A
        /// LIVE ONE. The shipped sequence is <c>SetFramebuffer(fb)</c>, <c>SetScissorRect(...)</c>, draw,
        /// <c>SetFramebuffer(fb)</c>, draw, and a backend that emits unconditionally renders the second draw
        /// outside the intended rectangle. That is golden-visible, and phase 2's first spec froze the wrong
        /// behaviour into its tally test, which would have made the test certify the bug.
        /// </summary>
        [Fact]
        public void ARedundantRebind_DoesNotRestoreTheFullScissorOverALiveOne()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);
            VulkanBoundFramebuffer framebuffer = Framebuffer(1, colour: 1, depth: true, width: 320, height: 240);

            schedule.SetFramebuffer(Buffer, framebuffer);
            schedule.SetScissorRect(0, 10, 20, 30, 40);
            schedule.PrepareDraw(Buffer);
            schedule.SetFramebuffer(Buffer, framebuffer);
            schedule.PrepareDraw(Buffer);

            Assert.Equal(new VulkanScissorRect(10, 20, 30, 40), Assert.Single(api.Scissors));
            Assert.Equal(new VulkanScissorRect(10, 20, 30, 40), schedule.Scissor);
        }

        /// <summary>
        /// AN EXPLICIT SCISSOR SET AFTER A FRAMEBUFFER CHANGE WINS, which is the subtle half of the marking rule.
        /// A change marks the scissor for emission, and emitting the FULL one at the draw would clobber the
        /// rectangle the caller set in between, reintroducing the same divergence from the other direction. What
        /// the draw emits is whatever the last writer left, and it emits it ONCE.
        /// </summary>
        [Fact]
        public void AnExplicitScissorAfterAFramebufferChange_IsWhatTheDrawEmits()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true, width: 320, height: 240));
            schedule.SetScissorRect(0, 4, 8, 16, 32);
            schedule.PrepareDraw(Buffer);

            Assert.Equal(new VulkanScissorRect(4, 8, 16, 32), Assert.Single(api.Scissors));
        }

        /// <summary>
        /// A REAL FRAMEBUFFER CHANGE DOES RESTORE THE FULL SCISSOR, at the NEW target's extent. This is the other
        /// direction of the same rule: a rectangle that survived a change would clip the next pass to a region
        /// chosen for a different target.
        /// </summary>
        [Fact]
        public void AFramebufferChange_RestoresTheFullScissorAtTheNewExtent()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true, width: 320, height: 240));
            schedule.SetScissorRect(0, 10, 20, 30, 40);
            schedule.SetFramebuffer(Buffer, Framebuffer(2, colour: 1, depth: true, width: 64, height: 64));
            schedule.PrepareDraw(Buffer);

            Assert.Equal(new VulkanScissorRect(0, 0, 64, 64), Assert.Single(api.Scissors));
        }

        /// <summary>
        /// AND <c>SetFullScissorRects</c> RESTORES IT WITHOUT A REBIND, which is the seam member that exists for
        /// exactly that.
        /// </summary>
        [Fact]
        public void SetFullScissorRects_RestoresTheBoundFramebuffersExtent()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true, width: 320, height: 240));
            schedule.SetScissorRect(0, 10, 20, 30, 40);
            schedule.SetFullScissorRects();
            schedule.PrepareDraw(Buffer);

            Assert.Equal(new VulkanScissorRect(0, 0, 320, 240), Assert.Single(api.Scissors));
        }

        /// <summary>
        /// A NON-ZERO SCISSOR INDEX IS REFUSED BY NAME rather than silently ignored, which is what the native
        /// Direct3D 11 backend does with the same index. Honouring it would mean enabling <c>multiViewport</c> and
        /// matching every pipeline's viewport count to its attachment count for a shape no shipped renderer has.
        /// </summary>
        [Fact]
        public void ANonZeroScissorIndex_IsRefused()
        {
            var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi());
            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 2, depth: false));

            Assert.Throws<ArgumentOutOfRangeException>(() => schedule.SetScissorRect(1, 0, 0, 8, 8));
        }

        // ---- The reset, and the refusals ----

        /// <summary>
        /// A RESET FORGETS THE BOUND FRAMEBUFFER, so the first bind of the next recording is a CHANGE. Keeping it
        /// would let that bind take the redundant path and draw into a target the fresh <c>VkCommandBuffer</c>
        /// never bound, with no viewport and no scissor, which is the failure mode that rasterises nothing.
        /// </summary>
        [Fact]
        public void Reset_MakesTheNextBindOfTheSameFramebufferAChangeAgain()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);
            VulkanBoundFramebuffer framebuffer = Framebuffer(1, colour: 1, depth: true);

            schedule.SetFramebuffer(Buffer, framebuffer);
            schedule.PrepareDraw(Buffer);

            schedule.Reset();
            Assert.False(schedule.BoundFramebuffer.IsBound);
            Assert.False(schedule.IsRendering);

            schedule.SetFramebuffer(Buffer, framebuffer);
            schedule.PrepareDraw(Buffer);

            Assert.Equal(2, api.Viewports.Count);
            Assert.Equal(2, api.Scissors.Count);
        }

        /// <summary>A reset DROPS pending clears, because they belong to a recording a <c>Begin</c> discarded.
        /// Flushing them into the next recording would clear a target that recording never asked about.</summary>
        [Fact]
        public void Reset_DropsPendingClearsRatherThanFlushingThem()
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: false));
            schedule.ClearColourTarget(Buffer, 0, Color.White);
            schedule.Reset();
            schedule.EndRendering(Buffer);

            Assert.Empty(api.Begins);
            Assert.Equal(0, api.EndCount);
        }

        /// <summary>Every member that needs a target refuses without one, because a render pass instance is opened
        /// from the bound framebuffer's own views and there is no default to fall back to.</summary>
        [Fact]
        public void WithNoFramebufferBound_TheMembersThatNeedOneRefuse()
        {
            var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi());

            Assert.Throws<InvalidOperationException>(() => schedule.ClearColourTarget(Buffer, 0, Color.White));
            Assert.Throws<InvalidOperationException>(() => schedule.ClearDepthStencil(Buffer, 1f));
            Assert.Throws<InvalidOperationException>(schedule.SetFullScissorRects);
            Assert.Throws<InvalidOperationException>(() => schedule.PrepareDraw(Buffer));
        }

        /// <summary>Clearing an attachment the bound framebuffer does not have is refused. A colour attachment
        /// index is also its shader output location, so a pass clearing one it does not have is describing a
        /// pipeline it cannot build.</summary>
        [Fact]
        public void ClearingAnAttachmentTheFramebufferDoesNotHave_IsRefused()
        {
            var schedule = new VulkanRenderingSchedule(new FakeVulkanRenderApi());
            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: false));

            Assert.Throws<ArgumentOutOfRangeException>(() => schedule.ClearColourTarget(Buffer, 1, Color.White));
            Assert.Throws<InvalidOperationException>(() => schedule.ClearDepthStencil(Buffer, 1f));
        }

        // ---- The stencil plane ----

        /// <summary>
        /// A COMBINED DEPTH-STENCIL FORMAT NAMES THE STENCIL PLANE TOO, in the begin and in a mid-pass clear.
        /// Dynamic rendering splits the planes into two attachment structures over one view, where a
        /// <c>VkRenderPass</c> took one attachment description with one aspect-wide load op, so leaving the
        /// stencil out would leave it holding whatever the last pass left. That is the determinism rule V-A6
        /// states for a store, applied to a load.
        /// </summary>
        [Theory]
        [InlineData(GpuPixelFormat.D32FloatS8UInt, true)]
        [InlineData(GpuPixelFormat.D24UNormS8UInt, true)]
        [InlineData(GpuPixelFormat.R32Float, false)]
        public void TheDepthAttachment_NamesAStencilPlaneOnlyForACombinedFormat(
            GpuPixelFormat format, bool expected)
        {
            var api = new FakeVulkanRenderApi();
            var schedule = new VulkanRenderingSchedule(api);

            schedule.SetFramebuffer(Buffer, Framebuffer(1, colour: 1, depth: true, depthFormat: format));
            schedule.ClearDepthStencil(Buffer, 1f);
            schedule.PrepareDraw(Buffer);
            schedule.ClearDepthStencil(Buffer, 0f);

            Assert.Equal(expected, Assert.NotNull(Assert.Single(api.Begins).Depth).Stencil);
            Assert.Equal(expected, Assert.Single(api.Clears).Stencil);
        }

        // ---- Fixtures ----

        // A framebuffer as the recorder holds it: plain data, so the whole schedule is drivable with no device,
        // no texture and no resource seam. The view and image handles are invented numbers derived from the id,
        // which is all a begin does with them.
        static VulkanBoundFramebuffer Framebuffer(ulong id, int colour, bool depth, uint width = 64,
            uint height = 64, GpuPixelFormat depthFormat = GpuPixelFormat.D32FloatS8UInt)
        {
            var attachments = new VulkanAttachment[colour];
            for (int i = 0; i < colour; i++)
            {
                attachments[i] = new VulkanAttachment(
                    id * 100 + (ulong)i + 1, id * 200 + (ulong)i + 1, GpuPixelFormat.R8G8B8A8UNorm,
                    DepthStencil: false);
            }

            VulkanAttachment depthAttachment = depth
                ? new VulkanAttachment(id * 100 + 99, id * 200 + 99, depthFormat, DepthStencil: true)
                : default;

            return new VulkanBoundFramebuffer(id, width, height, attachments, depthAttachment);
        }
    }
}
