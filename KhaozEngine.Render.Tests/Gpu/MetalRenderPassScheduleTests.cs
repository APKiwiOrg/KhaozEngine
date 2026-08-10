using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE DEFERRED BEGIN, DEVICE-FREE. Row 12 of
    /// <c>docs/design/METAL-NATIVE-BACKEND-DESIGN-2026-08-09.md</c>, sections 7.1 to 7.3.
    ///
    /// <para><b>EVERYTHING HERE IS A DECISION RATHER THAN A DRIVER CALL:</b> when a pass opens, which attachment
    /// a clear lands on (M-A2), what each load and store action is (M-A4), whether a pass with no draw still
    /// clears (M-A3), whether a framebuffer bind owes a viewport and a scissor and whether a REBIND of the same
    /// one does (M-A6), and what happens to all of that when an encoder boundary lands in the middle (M-R4). All
    /// of it runs on the Linux and Windows legs over two fakes handing out opaque numbers.</para>
    ///
    /// <para><b>THE FRAMEBUFFERS ARE BUILT AS PLAIN RECORDS rather than through
    /// <c>MetalFramebuffer</c></b>, which is what makes that possible: the real type is built from
    /// <c>MetalTexture</c>s and a texture needs a device. Its own construction rules (the size and sample-count
    /// match, the staging refusal, the stencil-plane read) need real textures and are
    /// <see cref="MetalRenderPassGpuTests"/>.</para>
    /// </summary>
    public sealed class MetalRenderPassScheduleTests
    {
        static readonly Color Red = new(1f, 0f, 0f, 1f);
        static readonly Color Green = new(0f, 1f, 0f, 1f);
        static readonly Color Blue = new(0f, 0f, 1f, 1f);

        // ---- The deferred begin (M-A1) -----------------------------------------------------------------------

        /// <summary>M-A1: binding a framebuffer opens nothing. A pass is a descriptor built at the FIRST DRAW, so
        /// a bind that opened an encoder would make every clear after it cost a boundary.</summary>
        [Fact]
        public void BindingAFramebufferOpensNoEncoder()
        {
            Harness h = Harness.New();

            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));

            Assert.False(h.Schedule.IsRendering);
            Assert.Equal(0, h.Encoders.EncoderBegins);
            Assert.Empty(h.Render.Passes);
        }

        /// <summary>The first draw is what opens it, and the second draw of the same pass opens nothing more.
        /// </summary>
        [Fact]
        public void TheFirstDrawOpensThePassAndTheSecondReusesIt()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));

            IntPtr first = h.Schedule.PrepareDraw();
            IntPtr second = h.Schedule.PrepareDraw();

            Assert.NotEqual(IntPtr.Zero, first);
            Assert.Equal(first, second);
            Assert.Equal(1, h.Encoders.EncoderBegins);
            Assert.Single(h.Render.Passes);
        }

        /// <summary>A draw with no framebuffer is refused by name rather than opening a pass against nothing. A
        /// fresh recording holds no framebuffer, so this is the very first thing a mis-sequenced frame does.
        /// </summary>
        [Fact]
        public void ADrawWithNoFramebufferIsRefusedByName()
        {
            Harness h = Harness.New();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Schedule.PrepareDraw());

            Assert.Contains("needs a framebuffer bound", thrown.Message, StringComparison.Ordinal);
        }

        // ---- The per-attachment clear (M-A2) -----------------------------------------------------------------

        /// <summary>
        /// M-A2, AND THE WHOLE REASON THIS ROW EXISTS. Three attachments cleared to three colours produce three
        /// <c>loadAction = Clear</c> entries with three DIFFERENT values, where the incumbent writes all three
        /// into slot 0 and leaves attachments 1 and 2 loading a texture nothing has written.
        /// </summary>
        [Fact]
        public void EveryClearedAttachmentGetsItsOwnLoadActionAndItsOwnValue()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 3));

            h.Schedule.ClearColourTarget(0, Red);
            h.Schedule.ClearColourTarget(1, Green);
            h.Schedule.ClearColourTarget(2, Blue);
            h.Schedule.PrepareDraw();

            RecordedRenderPass pass = Assert.Single(h.Render.Passes);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[0].LoadAction);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[1].LoadAction);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[2].LoadAction);
            Assert.Equal(Red, pass.Colour[0].ClearValue);
            Assert.Equal(Green, pass.Colour[1].ClearValue);
            Assert.Equal(Blue, pass.Colour[2].ClearValue);
        }

        /// <summary>
        /// THE KILL SWITCH, WHICH HAS TO REPRODUCE THE DEFECT EXACTLY OR THE GATE-1 A/B MEANS NOTHING. Under
        /// <c>KE_METAL_CLEAR=attachment0</c> the same three calls collapse onto slot 0, the LAST value wins
        /// there, and attachments 1 and 2 load.
        /// </summary>
        [Fact]
        public void TheIncumbentPositionCollapsesEveryClearOntoAttachmentZero()
        {
            Harness h = Harness.New(MetalClearMode.Attachment0);
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 3));

            h.Schedule.ClearColourTarget(0, Red);
            h.Schedule.ClearColourTarget(1, Green);
            h.Schedule.ClearColourTarget(2, Blue);
            h.Schedule.PrepareDraw();

            RecordedRenderPass pass = Assert.Single(h.Render.Passes);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[0].LoadAction);
            Assert.Equal(Blue, pass.Colour[0].ClearValue);
            Assert.Equal(MetalLoadAction.Load, pass.Colour[1].LoadAction);
            Assert.Equal(MetalLoadAction.Load, pass.Colour[2].LoadAction);
        }

        /// <summary>An attachment nobody cleared LOADS. There is no <c>DontCare</c> arm to reach by accident,
        /// which is M-A4's determinism rule applied to the load side.</summary>
        [Fact]
        public void AnAttachmentNobodyClearedLoads()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 2));

            h.Schedule.ClearColourTarget(1, Green);
            h.Schedule.PrepareDraw();

            RecordedRenderPass pass = Assert.Single(h.Render.Passes);
            Assert.Equal(MetalLoadAction.Load, pass.Colour[0].LoadAction);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[1].LoadAction);
        }

        /// <summary>Clearing an attachment the framebuffer does not have is refused by name. The index is also a
        /// shader output location, so a pass clearing one it lacks is writing to a location its own pipeline
        /// cannot declare.</summary>
        [Fact]
        public void ClearingAnAttachmentTheFramebufferLacksIsRefusedByName()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 2));

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => h.Schedule.ClearColourTarget(2, Red));

            Assert.Contains("2 colour attachment(s)", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A CLEAR THAT ARRIVES AFTER THE PASS OPENED ENDS IT AND GOES BACK ON THE PENDING ARRAY. Metal has no
        /// clear COMMAND, so there is no cheaper shape available, and this is exactly what the incumbent forces
        /// through <c>EnsureNoRenderPass</c> in its own <c>ClearColorTargetCore</c>. The cost is an encoder
        /// boundary, which M-T2's budget counts.
        /// </summary>
        [Fact]
        public void AClearAfterTheFirstDrawEndsThePassAndFoldsIntoTheNextOne()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.PrepareDraw();

            h.Schedule.ClearColourTarget(0, Red);

            Assert.False(h.Schedule.IsRendering);
            Assert.True(h.Schedule.HasPendingClears);

            h.Schedule.PrepareDraw();

            Assert.Equal(2, h.Encoders.EncoderBegins);
            Assert.Equal(MetalLoadAction.Load, h.Render.Passes[0].Colour[0].LoadAction);
            Assert.Equal(MetalLoadAction.Clear, h.Render.Passes[1].Colour[0].LoadAction);
        }

        // ---- The store action (M-A4) -------------------------------------------------------------------------

        /// <summary>
        /// M-A4: <c>Store</c> on EVERY attachment, colour and depth alike, chosen rather than defaulted. The
        /// descriptor's own default discards, so a plan that said nothing here would render a whole frame and
        /// throw it away.
        /// </summary>
        [Fact]
        public void EveryAttachmentStores()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 3, depth: true));

            h.Schedule.PrepareDraw();

            RecordedRenderPass pass = Assert.Single(h.Render.Passes);
            Assert.All(pass.Colour, a => Assert.Equal(MetalStoreAction.Store, a.StoreAction));
            Assert.Equal(MetalStoreAction.Store, pass.Depth.StoreAction);
        }

        /// <summary>The depth clear folds like a colour one, and the stencil flag travels with it so the
        /// descriptor knows whether to name a stencil attachment at all.</summary>
        [Fact]
        public void TheDepthClearFoldsAndCarriesTheStencilPlaneAnswer()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1, depth: true, depthHasStencil: true));

            h.Schedule.ClearDepthStencil(0.25f);
            h.Schedule.PrepareDraw();

            RecordedRenderPass pass = Assert.Single(h.Render.Passes);
            Assert.Equal(MetalLoadAction.Clear, pass.Depth.LoadAction);
            Assert.Equal(0.25f, pass.Depth.ClearDepth);
            Assert.True(pass.Depth.Stencil);
        }

        /// <summary>A framebuffer with no depth attachment describes none, and clearing depth on one is refused
        /// by name rather than silently describing a nil texture.</summary>
        [Fact]
        public void AFramebufferWithNoDepthDescribesNoneAndRefusesADepthClear()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Schedule.ClearDepthStencil(1f));
            Assert.Contains("declares no depth attachment", thrown.Message, StringComparison.Ordinal);

            h.Schedule.PrepareDraw();
            Assert.False(Assert.Single(h.Render.Passes).Depth.Present);
        }

        // ---- The clear-only pass (M-A3) ----------------------------------------------------------------------

        /// <summary>
        /// M-A3, FORCING SITE ONE OF TWO: <c>End</c>. A framebuffer plus a clear plus no draw must still CLEAR,
        /// which under a deferred begin is a begin and end pair with nothing between them. The incumbent forces
        /// exactly this and a golden depends on it.
        /// </summary>
        [Fact]
        public void AClearWithNoDrawStillClearsAtTheEndOfThePass()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.ClearColourTarget(0, Red);

            h.Schedule.EndPass();

            RecordedRenderPass pass = Assert.Single(h.Render.Passes);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[0].LoadAction);
            Assert.Equal(Red, pass.Colour[0].ClearValue);
            Assert.Equal(1, h.Encoders.EncoderBegins);
            Assert.False(h.Schedule.IsRendering);
        }

        /// <summary>M-A3, FORCING SITE TWO OF TWO: a framebuffer CHANGE. The outgoing framebuffer's clears are
        /// flushed before the incoming one is recorded, or they would be applied to the wrong target or to
        /// nothing.</summary>
        [Fact]
        public void AFramebufferChangeFlushesTheOutgoingFramebuffersUnusedClears()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.ClearColourTarget(0, Red);

            h.Schedule.SetFramebuffer(Framebuffer(2, colourCount: 1));

            RecordedRenderPass pass = Assert.Single(h.Render.Passes);
            Assert.Equal(MetalLoadAction.Clear, pass.Colour[0].LoadAction);
            Assert.Equal(Red, pass.Colour[0].ClearValue);
            Assert.False(h.Schedule.HasPendingClears);
        }

        /// <summary>
        /// A BEGIN CONSUMES THE PENDING ARRAY, WHICH IS WHAT MAKES THE FLUSH NEED NO "DID A DRAW HAPPEN" FLAG. A
        /// pass that drew does not clear a second time at its end.
        /// </summary>
        [Fact]
        public void APassThatDrewDoesNotClearAgainAtItsEnd()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.ClearColourTarget(0, Red);
            h.Schedule.PrepareDraw();

            Assert.False(h.Schedule.HasPendingClears);

            h.Schedule.EndPass();

            Assert.Single(h.Render.Passes);
            Assert.Equal(1, h.Encoders.EncoderBegins);
        }

        /// <summary>Ending a pass that is not open and owes nothing does nothing at all, so a caller never has to
        /// ask first.</summary>
        [Fact]
        public void EndingAnEmptyPassIsANoOp()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));

            h.Schedule.EndPass();

            Assert.Empty(h.Render.Passes);
            Assert.Equal(0, h.Encoders.EncoderBoundaries);
        }

        // ---- The viewport and the scissor (M-A6, M-A7) -------------------------------------------------------

        /// <summary>SECTION 7.3's FIRST ASSERTION: both are emitted at a framebuffer change. A backend that does
        /// not emit rasterises nothing, because there is no <c>SetViewport</c> on the seam at all.</summary>
        [Fact]
        public void AFramebufferChangeEmitsTheFullViewportAndTheFullScissor()
        {
            Harness h = Harness.New();
            h.Schedule.SetScissorTestEnabled(true);
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1, width: 800, height: 600));

            h.Schedule.PrepareDraw();

            Assert.Equal(new MetalViewportRect(0f, 0f, 800f, 600f, 0f, 1f), Assert.Single(h.Render.Viewports).Rect);
            Assert.Equal(new MetalScissorRect(0, 0, 800, 600), Assert.Single(h.Render.Scissors).Rect);
        }

        /// <summary>
        /// SECTION 7.3's SECOND ASSERTION, AND THE ONE PHASE 2's FIRST SPEC FROZE THE WRONG WAY: neither is
        /// emitted when the framebuffer did NOT change. This is the shipped sequence, and an unconditional emit
        /// silently restores the full scissor so the second draw renders outside the intended rectangle.
        /// </summary>
        [Fact]
        public void RebindingTheSameFramebufferDoesNotRestoreTheFullScissor()
        {
            Harness h = Harness.New();
            h.Schedule.SetScissorTestEnabled(true);
            MetalBoundFramebuffer fb = Framebuffer(1, colourCount: 1, width: 800, height: 600);

            h.Schedule.SetFramebuffer(fb);
            h.Schedule.SetScissorRect(0, 10, 20, 30, 40);
            h.Schedule.PrepareDraw();

            h.Schedule.SetFramebuffer(fb);
            h.Schedule.PrepareDraw();

            Assert.Equal(new MetalScissorRect(10, 20, 30, 40), Assert.Single(h.Render.Scissors).Rect);
            Assert.Single(h.Render.Viewports);
            Assert.Equal(1, h.Encoders.EncoderBegins);
        }

        /// <summary>
        /// SECTION 7.3's THIRD ASSERTION, WHICH IS METAL's OWN: the scissor is gated on the SEAM's
        /// <c>ScissorTestEnabled</c>. Metal has no scissor-test enable, so not reproducing the incumbent's gate
        /// would make a rectangle set before a pipeline with the test off apply here and not on Direct3D 11.
        /// </summary>
        [Fact]
        public void TheScissorIsNotEmittedWhenThePipelineHasTheTestOff()
        {
            Harness h = Harness.New();
            h.Schedule.SetScissorTestEnabled(false);
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1, width: 800, height: 600));
            h.Schedule.SetScissorRect(0, 10, 20, 30, 40);

            h.Schedule.PrepareDraw();

            Assert.Single(h.Render.Viewports);
            Assert.Empty(h.Render.Scissors);
        }

        /// <summary>
        /// AND A GATED-OUT RECTANGLE STAYS OWED, which is the half of the gate that is easy to get wrong in the
        /// other direction. The incumbent clears its own flag INSIDE the enabled branch, so the next pipeline
        /// with the test on receives the rectangle it should have had.
        /// </summary>
        [Fact]
        public void ARectangleGatedOutByOnePipelineIsEmittedToTheNextOneThatWantsIt()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1, width: 800, height: 600));
            h.Schedule.SetScissorRect(0, 10, 20, 30, 40);

            h.Schedule.SetScissorTestEnabled(false);
            h.Schedule.PrepareDraw();
            Assert.Empty(h.Render.Scissors);

            h.Schedule.SetScissorTestEnabled(true);
            h.Schedule.PrepareDraw();

            Assert.Equal(new MetalScissorRect(10, 20, 30, 40), Assert.Single(h.Render.Scissors).Rect);
        }

        /// <summary>Repeated rectangles between two draws collapse to ONE emission, and it is the last writer's.
        /// </summary>
        [Fact]
        public void RepeatedScissorWritesBetweenTwoDrawsCollapseToOneEmission()
        {
            Harness h = Harness.New();
            h.Schedule.SetScissorTestEnabled(true);
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1, width: 800, height: 600));
            h.Schedule.PrepareDraw();

            h.Schedule.SetScissorRect(0, 1, 2, 3, 4);
            h.Schedule.SetScissorRect(0, 5, 6, 7, 8);
            h.Schedule.PrepareDraw();

            Assert.Equal(2, h.Render.Scissors.Count);
            Assert.Equal(new MetalScissorRect(5, 6, 7, 8), h.Render.Scissors[^1].Rect);
        }

        /// <summary><c>SetFullScissorRects</c> restores the bound framebuffer's extent after an explicit
        /// rectangle, which is the seam member the engine calls to undo one.</summary>
        [Fact]
        public void SetFullScissorRectsRestoresTheFramebufferExtent()
        {
            Harness h = Harness.New();
            h.Schedule.SetScissorTestEnabled(true);
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1, width: 800, height: 600));
            h.Schedule.SetScissorRect(0, 1, 2, 3, 4);
            h.Schedule.PrepareDraw();

            h.Schedule.SetFullScissorRects();
            h.Schedule.PrepareDraw();

            Assert.Equal(new MetalScissorRect(0, 0, 800, 600), h.Render.Scissors[^1].Rect);
        }

        /// <summary>A non-zero scissor index is refused by name rather than silently ignored, which is what both
        /// native sibling backends do with the same index.</summary>
        [Fact]
        public void ANonZeroScissorIndexIsRefusedByName()
        {
            Harness h = Harness.New();

            ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => h.Schedule.SetScissorRect(1, 0, 0, 8, 8));

            Assert.Contains("ONE scissor rectangle, at index 0", thrown.Message, StringComparison.Ordinal);
        }

        /// <summary>Resetting the scissor with no framebuffer bound is refused, because the rectangle is derived
        /// FROM the framebuffer's extent and there is none to derive it from.</summary>
        [Fact]
        public void ResettingTheScissorWithNoFramebufferIsRefusedByName()
        {
            Harness h = Harness.New();

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                h.Schedule.SetFullScissorRects);

            Assert.Contains("needs a framebuffer bound", thrown.Message, StringComparison.Ordinal);
        }

        // ---- The encoder boundary (M-R4, M-A5) ---------------------------------------------------------------

        /// <summary>
        /// M-A5 AND M-R4 TOGETHER, AND THE REASON THIS TYPE KEEPS NO "IS A PASS OPEN" FLAG. A record-time upload
        /// opens a BLIT encoder, which ends the render encoder without the schedule being told. The next draw
        /// must reopen a pass AND re-emit the viewport and the scissor, because both are encoder state and the
        /// encoder they were set on is gone.
        /// </summary>
        [Fact]
        public void ABlitInTheMiddleOfAPassReopensItAndReEmitsTheDynamicState()
        {
            Harness h = Harness.New();
            h.Schedule.SetScissorTestEnabled(true);
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1, width: 800, height: 600));
            h.Schedule.PrepareDraw();

            Assert.Single(h.Render.Viewports);
            Assert.Single(h.Render.Scissors);

            // What MetalCommandList.UpdateBuffer does for a payload big enough to take the staging path (M-M8).
            h.Scope.EnsureBlitEncoder();

            Assert.False(h.Schedule.IsRendering);
            Assert.True(h.Schedule.ViewportOwed);
            Assert.True(h.Schedule.ScissorOwed);

            h.Schedule.PrepareDraw();

            Assert.Equal(2, h.Render.Passes.Count);
            Assert.Equal(2, h.Render.Viewports.Count);
            Assert.Equal(2, h.Render.Scissors.Count);
        }

        /// <summary>And the reopened pass LOADS every attachment. The clears were consumed by the first begin, so
        /// re-clearing here would erase whatever the first half of the pass drew.</summary>
        [Fact]
        public void AReopenedPassLoadsRatherThanClearingAgain()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.ClearColourTarget(0, Red);
            h.Schedule.PrepareDraw();

            h.Scope.EnsureBlitEncoder();
            h.Schedule.PrepareDraw();

            Assert.Equal(MetalLoadAction.Clear, h.Render.Passes[0].Colour[0].LoadAction);
            Assert.Equal(MetalLoadAction.Load, h.Render.Passes[1].Colour[0].LoadAction);
        }

        // ---- Descriptor ownership and the orphan target ------------------------------------------------------

        /// <summary>
        /// EXACTLY ONE RELEASE PER DESCRIPTOR, at every exit. The descriptor arrives retained because it has to
        /// outlive the call that built it and reach the encoder's begin, so an unreleased one is a leaked
        /// Objective-C object on a path that runs once per pass per frame.
        /// </summary>
        [Fact]
        public void EveryDescriptorIsReleasedExactlyOnce()
        {
            Harness h = Harness.New();
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.PrepareDraw();
            h.Scope.EnsureBlitEncoder();
            h.Schedule.PrepareDraw();
            h.Schedule.EndPass();

            Assert.Equal(2, h.Render.Passes.Count);
            Assert.Equal(0, h.Render.OutstandingDescriptors);
            Assert.Equal(0, h.Render.UnbalancedDescriptorReleases);
        }

        /// <summary>
        /// M-W5's ORPHAN CASE: an encoder that comes back nil is a frame that renders nowhere rather than a
        /// throw, and the descriptor is STILL released. That exit is the one a naive implementation misses,
        /// because it is the only one that does not go through the success path.
        /// </summary>
        [Fact]
        public void ANilEncoderReleasesItsDescriptorAndLeavesTheClearsOwed()
        {
            Harness h = Harness.New();
            h.Encoders.NilForKind = MetalEncoderKind.Render;
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.ClearColourTarget(0, Red);

            Assert.Equal(IntPtr.Zero, h.Schedule.PrepareDraw());

            Assert.Equal(0, h.Render.OutstandingDescriptors);
            Assert.False(h.Schedule.IsRendering);
            Assert.True(h.Schedule.HasPendingClears);
        }

        /// <summary>
        /// A DESCRIPTOR METAL WOULD NOT BUILD AT ALL OWES NO RELEASE AND NEVER REACHES THE ENCODER FACTORY, and
        /// the second half is the one this row exists for. <c>renderCommandEncoderWithDescriptor:</c> takes a
        /// nonnull argument, so handing it nil is undefined rather than a refusal it reports, and the shape that
        /// hides it is exactly this test's fake: a sink asked for an encoder with a nil descriptor will
        /// obligingly hand back a perfectly good one. The observable outcome is the same as M-W5's orphan case (a
        /// draw that goes nowhere with its clears still owed) and the path to it is not.
        /// </summary>
        [Fact]
        public void ARefusedDescriptorOwesNoReleaseAndOpensNoEncoder()
        {
            Harness h = Harness.New();
            h.Render.NextCreateFails = true;
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.ClearColourTarget(0, Red);

            Assert.Equal(IntPtr.Zero, h.Schedule.PrepareDraw());

            Assert.Equal(0, h.Encoders.EncoderBegins);
            Assert.Equal(0, h.Render.OutstandingDescriptors);
            Assert.Equal(0, h.Render.UnbalancedDescriptorReleases);
            Assert.True(h.Schedule.HasPendingClears);
        }

        // ---- Reset -------------------------------------------------------------------------------------------

        /// <summary>A reset drops the bound framebuffer, the pending clears and the scissor-test gate, which is
        /// what a fresh command buffer holds. The clears belong to a recording a <c>Begin</c> discarded by
        /// contract.</summary>
        [Fact]
        public void ResetForgetsTheFramebufferThePendingClearsAndTheGate()
        {
            Harness h = Harness.New();
            h.Schedule.SetScissorTestEnabled(true);
            h.Schedule.SetFramebuffer(Framebuffer(1, colourCount: 1));
            h.Schedule.ClearColourTarget(0, Red);

            h.Schedule.Reset();

            Assert.False(h.Schedule.BoundFramebuffer.IsBound);
            Assert.False(h.Schedule.HasPendingClears);
            Assert.False(h.Schedule.ScissorTestEnabled);
            Assert.Empty(h.Render.Passes);
        }

        // ---- Fixtures ----------------------------------------------------------------------------------------

        static MetalBoundFramebuffer Framebuffer(ulong id, int colourCount, bool depth = false,
            bool depthHasStencil = false, uint width = 64, uint height = 64)
        {
            var colour = new MetalAttachment[colourCount];
            for (int i = 0; i < colourCount; i++)
            {
                colour[i] = new MetalAttachment(new IntPtr(0x2000 + (int)(id * 16) + i),
                    GpuPixelFormat.B8G8R8A8UNorm);
            }

            MetalAttachment depthAttachment = depth
                ? new MetalAttachment(new IntPtr(0x3000 + (int)id),
                    depthHasStencil ? GpuPixelFormat.D32FloatS8UInt : GpuPixelFormat.R32Float)
                : default;

            return new MetalBoundFramebuffer(id, width, height, colour, depthAttachment,
                depth && depthHasStencil);
        }

        // The schedule over the two fakes plus a REAL encoder scope, because the interesting rules are relations
        // between the schedule and the scope: whether a pass is open, whether a boundary invalidated the
        // viewport, and whether a blit somewhere else in the backend ended the encoder underneath it.
        sealed class Harness
        {
            Harness(MetalRenderPassSchedule schedule, MetalEncoderScope scope, FakeMetalEncoderCalls encoders,
                FakeMetalRenderCalls render)
            {
                Schedule = schedule;
                Scope = scope;
                Encoders = encoders;
                Render = render;
            }

            internal MetalRenderPassSchedule Schedule { get; }

            internal MetalEncoderScope Scope { get; }

            internal FakeMetalEncoderCalls Encoders { get; }

            internal FakeMetalRenderCalls Render { get; }

            internal static Harness New(MetalClearMode mode = MetalClearMode.PerAttachment)
            {
                FakeMetalEncoderCalls encoders = new();
                FakeMetalRenderCalls render = new();
                MetalEncoderScope scope = new(new FakeMetalEncoderSink(encoders));

                // The command buffer a real Begin would have adopted. Opaque, because nothing here dereferences
                // it: it exists so the scope has a recording in flight to open encoders on.
                scope.BeginRecording(new IntPtr(0x100));

                return new Harness(
                    new MetalRenderPassSchedule(scope, new FakeMetalRenderApi(render), mode), scope, encoders,
                    render);
            }
        }
    }
}
