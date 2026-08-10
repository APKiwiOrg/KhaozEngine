using System;
using System.Globalization;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DEFERRED BEGIN, DECISIONS M-A1 TO M-A7 (section 7), and nothing else. It decides WHEN a render encoder
    /// opens, what each attachment's <c>loadAction</c> is when it does, which attachment a clear lands on, when a
    /// clear that arrives too late costs an encoder boundary, when a pass with no draw must still clear, and
    /// whether a framebuffer bind owes a viewport and a scissor. Every one of those is a decision that can be
    /// WRONG, and every one of them runs under a plain <c>[Fact]</c> on a machine with no Metal at all, because
    /// the four native calls it makes go through <see cref="IMetalRenderApi"/> and the boundaries through
    /// <see cref="MetalEncoderScope"/>.
    ///
    /// <list type="number">
    /// <item><description><b>The state is four things:</b> the bound framebuffer, a pending clear value per
    /// colour attachment plus one for depth, the viewport and scissor a draw would emit, and the epoch each of
    /// those two was last emitted in. Whether a pass is OPEN is deliberately not among them, and the next
    /// paragraph is why.</description></item>
    /// <item><description><b><see cref="SetFramebuffer"/>.</b> A rebind of the framebuffer already bound does
    /// NOTHING AT ALL. A change ends any open pass, flushes the outgoing framebuffer's pending clears if no draw
    /// consumed them, records the new one, drops the pending array, and marks the viewport and the scissor for
    /// emission (M-A6).</description></item>
    /// <item><description><b><see cref="ClearColourTarget"/> and <see cref="ClearDepthStencil"/>.</b> Before the
    /// pass opens, the value is stored as pending and becomes <c>loadAction = Clear</c> on the attachment the
    /// caller NAMED (M-A2). After it has opened, the RENDER pass is ended and the value stored as pending anyway,
    /// which is what the incumbent forces through <c>EnsureNoRenderPass</c> in its own
    /// <c>ClearColorTargetCore</c>: Metal has no clear COMMAND, so a clear arriving mid-pass costs a whole
    /// encoder boundary and there is no cheaper shape available. A blit or compute encoder open at the time is
    /// left alone, which is that same helper's semantics and not <c>EnsureNoEncoder</c>'s.</description></item>
    /// <item><description><b><see cref="PrepareDraw"/>, which the first draw of a pass calls.</b> Open the
    /// encoder with <c>loadAction = Clear</c> and the pending value per attachment that has one, <c>Load</c> for
    /// the rest and <c>storeAction = Store</c> always (M-A4), then emit whatever dynamic state is
    /// owed.</description></item>
    /// <item><description><b><see cref="EndPass"/>, which <c>End</c> and <see cref="SetFramebuffer"/> call.</b>
    /// Close the pass, flushing pending clears through a begin and end pair first if there were any and no draw
    /// came (M-A3).</description></item>
    /// </list>
    ///
    /// <para><b>THERE IS NO "IS A PASS OPEN" FLAG, AND ITS ABSENCE IS THE ONE STRUCTURAL DIFFERENCE FROM THE
    /// VULKAN SIBLING.</b> That schedule keeps a <c>_rendering</c> bool because <c>vkCmdBeginRendering</c> is the
    /// only thing that can open or close a render pass instance, so the flag cannot get out of step. Here it
    /// could: a record-time <c>UpdateBuffer</c> large enough to take the staging path opens a BLIT encoder
    /// (M-M8), which ends the render encoder without this type being told, and 2.1 is the whole section about how
    /// expensive and how ordinary that is. A duplicate flag would then say a pass was open while the encoder
    /// underneath it had gone, and every subsequent draw would be recorded into a dead handle. So the question is
    /// asked of <see cref="MetalEncoderScope.Open"/>, which is the one owner of every transition, and the answer
    /// cannot be stale by construction.</para>
    ///
    /// <para><b>AND THAT IS ALSO WHY THE VIEWPORT AND THE SCISSOR ARE TRACKED WITH
    /// <see cref="MetalEncoderMark"/> RATHER THAN WITH DIRTY BOOLS (M-R4).</b> Both are ENCODER state, so a pass
    /// split by a blit and reopened owes both again even though nothing about the framebuffer changed. An epoch
    /// stamp answers "is this still what the encoder holds" for free, where a bool would need every path that can
    /// end an encoder to remember to set it, which is exactly the registration-time forgetting the stamp
    /// mechanism was chosen over.</para>
    ///
    /// <para><b>THE FRAMEBUFFER-CHANGE GUARD WRAPS THE WHOLE OF <see cref="SetFramebuffer"/>, WHICH IS WHAT
    /// VELDRID'S BASE CLASS DOES (M-A6).</b> There is no <c>SetViewport</c> on the seam at all: the engine gets a
    /// viewport because <c>CommandList.SetFramebuffer</c> auto-calls <c>SetFullViewports</c> and
    /// <c>SetFullScissorRects</c>, and the whole body sits inside an <c>if (_framebuffer != fb)</c> identity
    /// guard. BOTH halves have to be reproduced. A backend that does not emit rasterises nothing. A backend that
    /// emits UNCONDITIONALLY diverges on the shipped sequence <c>SetFramebuffer(fb)</c>,
    /// <c>SetScissorRect(...)</c>, draw, <c>SetFramebuffer(fb)</c>, draw, where the second bind silently restores
    /// the full scissor and the second draw renders outside the intended rectangle. That is golden-visible, and
    /// phase 2's first spec froze the wrong behaviour into its tally test.</para>
    ///
    /// <para><b>AND THE MARKED VIEWPORT AND SCISSOR ARE VALUES RATHER THAN A "RE-EMIT THE FULL ONE" FLAG</b>,
    /// which is the subtle half of the same rule. Deferring the emission to the first draw and then emitting the
    /// FULL scissor there would clobber a rectangle the caller set in between, reintroducing the divergence from
    /// the other direction. So a framebuffer change SETS the pending scissor to the full extent and
    /// <see cref="SetScissorRect"/> overwrites it, and what a draw emits is whatever the last writer left.</para>
    ///
    /// <para><b>THE SCISSOR HAS A THIRD HALF METAL ADDS, AND IT IS THE INCUMBENT'S OWN (M-A6).</b>
    /// <c>PreDrawCommand</c> flushes the scissor only when <c>_graphicsPipeline.ScissorTestEnabled</c>, so a
    /// pipeline with the test off never receives a rectangle at all. Metal has no scissor-test enable (the rect
    /// is always live, defaulting to the full attachment), so reproducing the gate is the backend honouring the
    /// SEAM's own rasterizer state rather than the API's, and Direct3D 11 honours the same flag through a real
    /// enable bit. NOT reproducing it would make a scissor set before a pipeline with the test off apply here and
    /// not there. A gated-out emission stays OWED rather than being consumed, which is the incumbent's own shape:
    /// its flag is cleared inside the branch, so the next pipeline with the test on receives the rectangle it
    /// should have had.</para>
    ///
    /// <para><b>M-A5's END-BEFORE-ANYTHING-ILLEGAL DOES NOT NEED A MEMBER HERE, and that is worth saying because
    /// its absence looks like an omission.</b> Every command illegal inside a render encoder opens a different
    /// encoder kind (a dispatch a compute one, a copy or a mip chain or a resolve a blit one), and each of those
    /// goes through <see cref="MetalEncoderScope"/>, whose first act is to end whatever is open. So the
    /// invariant is the scope's and is already enforced for callers that have not been written yet. What this
    /// type owes is that it OBSERVES the result rather than contradicting it, which is the no-flag rule above and
    /// which has its own device-free row.</para>
    ///
    /// <para><b>NOTHING HERE IS SYNCHRONISED</b>, on the same grounds as the list that owns it: one list records
    /// on one thread at a time and this schedule is that list's alone.</para>
    /// </summary>
    internal sealed class MetalRenderPassSchedule
    {
        readonly MetalEncoderScope _encoders;
        readonly IMetalRenderApi _api;
        readonly MetalClearMode _clearMode;

        // GROWN TO THE WIDEST FRAMEBUFFER EVER BOUND rather than reallocated per bind, and cleared rather than
        // replaced on a change, so a frame that alternates between two framebuffers allocates nothing.
        PendingClear[] _colourClears = new PendingClear[4];
        MetalColourAttachment[] _beginColour = new MetalColourAttachment[4];

        MetalBoundFramebuffer _framebuffer;
        MetalViewportRect _viewport;
        MetalScissorRect _scissor;

        // THE EPOCH EACH WAS LAST EMITTED IN (M-R4). Not a bool: both are encoder state, so a boundary owes both
        // again, and Clear() is what an explicit rewrite uses.
        MetalEncoderMark _viewportEmitted;
        MetalEncoderMark _scissorEmitted;

        float _depthClear;
        bool _depthClearPending;
        bool _scissorTestEnabled;

        /// <param name="encoders">The list's encoder scope, which owns every transition and is the ONLY place
        /// this type reads "is a pass open" from. See the type remarks.</param>
        /// <param name="api">The four native rendering calls. Real on a device, a recording fake in the
        /// device-free tests.</param>
        /// <param name="clearMode">M-A2's position for this recording, captured once so a recording cannot
        /// straddle two policies. See <see cref="MetalClearPolicy"/>.</param>
        internal MetalRenderPassSchedule(MetalEncoderScope encoders, IMetalRenderApi api,
            MetalClearMode clearMode = MetalClearMode.PerAttachment)
        {
            ArgumentNullException.ThrowIfNull(encoders);
            ArgumentNullException.ThrowIfNull(api);

            _encoders = encoders;
            _api = api;
            _clearMode = clearMode;
        }

        /// <summary>Whether a render encoder is currently open, asked of the scope rather than remembered. The
        /// one piece of state that decides whether a clear folds or costs a boundary.</summary>
        internal bool IsRendering => _encoders.Open == MetalEncoderKind.Render;

        /// <summary>The bound framebuffer as plain data, default when none is bound.</summary>
        internal MetalBoundFramebuffer BoundFramebuffer => _framebuffer;

        /// <summary>M-A2's position this recording was built with.</summary>
        internal MetalClearMode ClearMode => _clearMode;

        /// <summary>Whether any attachment is owed a <c>loadAction = Clear</c>. A begin consumes these, so this
        /// reading true at the end of a pass is exactly the clear-only case (M-A3).</summary>
        internal bool HasPendingClears
        {
            get
            {
                if (_depthClearPending) return true;
                for (int i = 0; i < _framebuffer.ColourCount && i < _colourClears.Length; i++)
                {
                    if (_colourClears[i].Pending) return true;
                }

                return false;
            }
        }

        /// <summary>The viewport a draw would emit, whether or not it is owed one.</summary>
        internal MetalViewportRect Viewport => _viewport;

        /// <summary>The scissor rectangle a draw would emit.</summary>
        internal MetalScissorRect Scissor => _scissor;

        /// <summary>Whether the next draw owes a <c>setViewports:count:</c>, which is true after a framebuffer
        /// change and after ANY encoder boundary since the last emission.</summary>
        internal bool ViewportOwed => !_viewportEmitted.IsValidIn(_encoders.Epoch);

        /// <summary>Whether the next draw owes a <c>setScissorRects:count:</c>. The gate is NOT part of this: a
        /// rectangle owed to a pipeline with the scissor test off stays owed.</summary>
        internal bool ScissorOwed => !_scissorEmitted.IsValidIn(_encoders.Epoch);

        /// <summary>Whether the bound graphics pipeline has the seam's <c>ScissorTestEnabled</c> set, which is
        /// what gates the scissor emission. False until a pipeline says otherwise, which is also the state in
        /// which no draw is possible.</summary>
        internal bool ScissorTestEnabled => _scissorTestEnabled;

        /// <summary>
        /// TELL THE SCHEDULE WHAT THE NEWLY BOUND PIPELINE'S <c>ScissorTestEnabled</c> IS. Row 11
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/577) calls this from <c>SetPipeline</c>, with
        /// <c>GpuPipelineDescription.Rasterizer.ScissorTestEnabled</c>, and nothing else calls it.
        /// <para>
        /// IT DOES NOT MARK THE SCISSOR. A pipeline switch from test-off to test-on emits the rectangle anyway,
        /// because a gated-out emission was never consumed and is therefore still owed, which is the incumbent's
        /// own shape rather than a rule added here.
        /// </para>
        /// </summary>
        internal void SetScissorTestEnabled(bool enabled) => _scissorTestEnabled = enabled;

        /// <summary>
        /// THE FRAMEBUFFER BIND, WHOLE-METHOD IDENTITY GUARD AND ALL (M-A6). See the type remarks for why the
        /// guard covers everything rather than the viewport alone.
        /// </summary>
        /// <param name="framebuffer">The incoming framebuffer as plain data.</param>
        internal void SetFramebuffer(in MetalBoundFramebuffer framebuffer)
        {
            if (framebuffer.Id == _framebuffer.Id) return;

            // THE OUTGOING FRAMEBUFFER'S PASS CLOSES FIRST, clear-only flush included, which is the same helper
            // End uses and one of the incumbent's two forcing sites (M-A3). Opening a pass against the incoming
            // attachments with the outgoing encoder still open is a call Metal refuses outright.
            EndPass();

            _framebuffer = framebuffer;

            EnsureColourCapacity(framebuffer.ColourCount);
            Array.Clear(_colourClears, 0, _colourClears.Length);
            _depthClearPending = false;

            _viewport = MetalViewportRect.ForFramebuffer(framebuffer.Width, framebuffer.Height);
            _scissor = MetalScissorRect.ForFramebuffer(framebuffer.Width, framebuffer.Height);
            _viewportEmitted.Clear();
            _scissorEmitted.Clear();
        }

        /// <summary>
        /// A COLOUR CLEAR, WHICH FOLDS OR COSTS A BOUNDARY DEPENDING ON WHETHER THE PASS IS ALREADY OPEN (M-A2).
        /// <para>
        /// THE INDEX IT LANDS ON IS THE CALLER'S, unless <c>KE_METAL_CLEAR=attachment0</c> put this recording on
        /// the incumbent's position, and that ONE substitution is the whole of M-A2's kill switch. See
        /// <see cref="MetalClearPolicy"/>.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">No framebuffer is bound.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The framebuffer has no colour attachment at that
        /// index.</exception>
        internal void ClearColourTarget(uint index, Color rgba)
        {
            RequireFramebuffer("Clearing a colour target");

            if (index >= (uint)_framebuffer.ColourCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "The bound native Metal framebuffer has "
                    + _framebuffer.ColourCount.ToString(CultureInfo.InvariantCulture)
                    + " colour attachment(s), so there is nothing at that index to clear. A colour attachment "
                    + "index is also its shader output location, so a pass clearing one it does not have is "
                    + "writing to a location its own pipeline cannot declare.");
            }

            EndOpenPass();

            _colourClears[MetalClearPolicy.TargetIndex(_clearMode, index)] = new PendingClear(Pending: true, rgba);
        }

        /// <summary>
        /// THE DEPTH CLEAR, same fold-or-boundary rule. The stencil plane goes with it at zero on a combined
        /// format, for the reason <see cref="MetalDepthAttachment"/> gives.
        /// </summary>
        /// <exception cref="InvalidOperationException">No framebuffer is bound, or the bound one declares no
        /// depth attachment.</exception>
        internal void ClearDepthStencil(float depth)
        {
            RequireFramebuffer("Clearing the depth attachment");

            if (!_framebuffer.HasDepth)
            {
                throw new InvalidOperationException(
                    "The bound native Metal framebuffer declares no depth attachment, so there is nothing to "
                    + "clear. A framebuffer's attachments are fixed at creation, so this is the pass binding the "
                    + "wrong target rather than a depth attachment that failed to arrive.");
            }

            EndOpenPass();

            _depthClear = depth;
            _depthClearPending = true;
        }

        /// <summary>
        /// An explicit scissor rectangle, which overwrites whatever the last writer left and is emitted by the
        /// next draw whose pipeline has the scissor test on.
        /// <para>
        /// AND IT IS THE ONE RENDERING MEMBER THAT DOES NOT REQUIRE A FRAMEBUFFER, deliberately. It takes every
        /// value it stores from the caller and reads nothing off the bound framebuffer, where
        /// <see cref="SetFullScissorRects"/> derives its rectangle FROM that framebuffer's extent and therefore
        /// cannot answer without one. A rectangle recorded with no target is only ever consumed by a draw, and
        /// <see cref="PrepareDraw"/> refuses without one, so the missing target is caught where it first matters.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not 0.</exception>
        internal void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            RequireSingleScissor(index);

            _scissor = new MetalScissorRect(x, y, width, height);
            _scissorEmitted.Clear();
        }

        /// <summary>Restore the scissor to the bound framebuffer's full extent, which is what a framebuffer
        /// change applies and what this restores after an explicit rectangle.</summary>
        /// <exception cref="InvalidOperationException">No framebuffer is bound.</exception>
        internal void SetFullScissorRects()
        {
            RequireFramebuffer("Resetting the scissor rects");

            _scissor = MetalScissorRect.ForFramebuffer(_framebuffer.Width, _framebuffer.Height);
            _scissorEmitted.Clear();
        }

        /// <summary>
        /// THE PRE-DRAW HOOK: open the pass if it is not open, then emit whatever dynamic state is owed. Row 14
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/580) calls this FIRST in <c>Draw</c> and
        /// <c>DrawIndexed</c>, before row 13's bind flush and the vertex binds, and issues into the encoder this
        /// returns.
        /// <para>
        /// THE ORDER INSIDE IS FIXED. The encoder opens before the viewport and the scissor go out, because both
        /// are encoder state and there is nothing to set them on until it exists.
        /// </para>
        /// <para>
        /// IT RETURNS <see cref="IntPtr.Zero"/> RATHER THAN THROWING WHEN THE ENCODER WOULD NOT OPEN, which is
        /// M-W5's orphan-target case: the caller's draw goes nowhere and the frame still counts. Row 14's draws
        /// return early on it.
        /// </para>
        /// </summary>
        /// <returns>The open render encoder, or <see cref="IntPtr.Zero"/>.</returns>
        /// <exception cref="InvalidOperationException">No framebuffer is bound.</exception>
        internal IntPtr PrepareDraw()
        {
            RequireFramebuffer("Drawing");

            IntPtr encoder = BeginPass();
            if (encoder == IntPtr.Zero) return IntPtr.Zero;

            if (ViewportOwed)
            {
                _api.SetViewport(encoder, _viewport.X, _viewport.Y, _viewport.Width, _viewport.Height,
                    _viewport.MinDepth, _viewport.MaxDepth);
                _viewportEmitted.Mark(_encoders.Epoch);
            }

            // THE GATE IS THE SEAM'S RASTERIZER STATE, NOT METAL'S (M-A6), and a gated-out rectangle is NOT
            // marked: it stays owed, so the next pipeline with the test on receives it. The incumbent clears its
            // own flag inside this same branch for the same reason.
            if (_scissorTestEnabled && ScissorOwed)
            {
                _api.SetScissorRect(encoder, _scissor.X, _scissor.Y, _scissor.Width, _scissor.Height);
                _scissorEmitted.Mark(_encoders.Epoch);
            }

            return encoder;
        }

        /// <summary>
        /// CLOSE THE PASS, FLUSHING A CLEAR-ONLY ONE FIRST (M-A3). One of the incumbent's two forcing sites is
        /// <c>SetFramebufferCore</c> and the other is <c>End</c>, and those are exactly the two callers of this.
        /// <para>
        /// THE CLEAR-ONLY CASE IS REPRODUCED DELIBERATELY, NOT INHERITED BY ACCIDENT. A framebuffer plus a clear
        /// plus an <c>End</c> with no draw between them must still clear, because the incumbent forces exactly
        /// that and a golden depends on it. Under a deferred begin that is a begin and end pair with no draws,
        /// and it is the ONE place the deferral needs an explicit flush rather than falling out of the schedule.
        /// It needs no "did a draw happen" flag to detect: a begin CONSUMES the pending array, so a pending clear
        /// still sitting there is itself the proof that no draw came.
        /// </para>
        /// <para>
        /// IT IS SAFE TO CALL WHEN NOTHING IS OPEN AND NOTHING IS PENDING, and does nothing then, so a caller
        /// never has to ask first.
        /// </para>
        /// </summary>
        internal void EndPass()
        {
            if (!IsRendering && HasPendingClears && _framebuffer.IsBound) BeginPass();

            EndOpenPass();
        }

        /// <summary>
        /// FORGET EVERYTHING, which is what a fresh <c>MTLCommandBuffer</c> holds: no framebuffer, no open pass,
        /// no dynamic state and no pending clears. Called from <c>MetalCommandList.Begin</c>, in the one reset
        /// block that file keeps.
        /// <para>
        /// THE PENDING CLEARS GO TOO, and dropping them is correct rather than lossy: they belong to a recording
        /// that was discarded, and a <c>Begin</c> discards a recording by contract.
        /// </para>
        /// <para>
        /// THE SCISSOR-TEST GATE GOES WITH THEM, because it describes a pipeline bound in the recording that has
        /// just been thrown away and no pipeline is bound in the new one.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            _framebuffer = default;
            _depthClearPending = false;
            _depthClear = 0f;
            _scissorTestEnabled = false;
            _viewport = default;
            _scissor = default;
            _viewportEmitted.Clear();
            _scissorEmitted.Clear();
            Array.Clear(_colourClears, 0, _colourClears.Length);
        }

        // END A RENDER ENCODER AND NOTHING ELSE, which is EnsureNoRenderPass's semantics rather than
        // EnsureNoEncoder's. The difference shows up on the clear path: a clear arriving while a record-time
        // upload's BLIT encoder is open must not close that encoder, because the clear does not need it closed
        // (it only needs to not be inside a render pass) and closing it costs a boundary M-T2's budget counts
        // plus a reopen on the next upload. The clear still lands correctly either way: it goes on the pending
        // array, and the BeginPass that consumes it ends whatever is open on its way in.
        void EndOpenPass()
        {
            if (IsRendering) _encoders.EnsureNoEncoder();
        }

        /// <summary>
        /// OPEN THE PASS IF IT IS NOT OPEN: one load action per attachment, <c>storeAction = Store</c> always
        /// (M-A4), and the pending array consumed. Reached from the first draw of a pass and from the clear-only
        /// flush, and from nowhere else.
        /// <para>
        /// THE DESCRIPTOR IS RELEASED IN A <c>finally</c>, including on the path where the encoder came back nil,
        /// because it arrives retained and the ownership rule is exactly one release per acquisition at every
        /// exit. That is the same rule the encoder itself is under and the same failure if it is broken: a leaked
        /// object holding a reference to something the queue is counting.
        /// </para>
        /// <para>
        /// THE PENDING ARRAY IS CONSUMED ONLY ON A SUCCESSFUL OPEN. A descriptor that never became an encoder
        /// applied nothing, so the clears are still owed, and the next draw or the <c>End</c> after it will
        /// carry them.
        /// </para>
        /// </summary>
        IntPtr BeginPass()
        {
            if (IsRendering) return _encoders.Current;

            ReadOnlySpan<MetalAttachment> colour = _framebuffer.ColourAttachments;
            EnsureColourCapacity(colour.Length);

            for (int i = 0; i < colour.Length; i++)
            {
                PendingClear pending = _colourClears[i];
                _beginColour[i] = new MetalColourAttachment(
                    colour[i].Texture,
                    pending.Pending ? MetalLoadAction.Clear : MetalLoadAction.Load,
                    pending.Value,
                    MetalStoreAction.Store);
            }

            var depth = new MetalDepthAttachment(
                _framebuffer.Depth.Texture,
                _depthClearPending ? MetalLoadAction.Clear : MetalLoadAction.Load,
                _depthClear,
                MetalStoreAction.Store,
                _framebuffer.DepthHasStencil);

            IntPtr descriptor = _api.CreateRenderPassDescriptor(_beginColour.AsSpan(0, colour.Length), in depth);

            // A NIL DESCRIPTOR IS NOT HANDED ON, and this is the one arm the design did not predict. M-W5's
            // orphan case is about the ENCODER coming back nil, so the first draft ran both through one path and
            // let the nil descriptor reach the encoder factory. That is not the same failure:
            // renderCommandEncoderWithDescriptor: takes a nonnull argument, so passing nil is undefined rather
            // than a documented refusal, and the device-free row that found this had a fake obligingly returning
            // a perfectly good encoder for it. Refusing here makes both shapes the same OBSERVABLE outcome (a
            // draw that goes nowhere, clears still owed) through two different code paths, which is what the two
            // situations actually are.
            //
            // THIS RETURN IS NOW THE ONLY LEGITIMATE HANDLING of a descriptor Metal would not build, rather than
            // the only protection: MetalEncoderScope.EnsureRenderEncoder REFUSES a nil descriptor by name, so a
            // later row that builds its own descriptor and skips this arm is caught at the transition instead of
            // reaching the driver.
            if (descriptor == IntPtr.Zero) return IntPtr.Zero;

            IntPtr encoder;
            try
            {
                encoder = _encoders.EnsureRenderEncoder(descriptor);
            }
            finally
            {
                _api.ReleaseRenderPassDescriptor(descriptor);
            }

            if (encoder == IntPtr.Zero) return IntPtr.Zero;

            // CONSUMED, which is what makes a pending clear at the end of a pass mean "no draw came" with no flag
            // to keep in step.
            Array.Clear(_colourClears, 0, _colourClears.Length);
            _depthClearPending = false;

            return encoder;
        }

        void EnsureColourCapacity(int required)
        {
            if (required <= _colourClears.Length) return;

            int capacity = _colourClears.Length;
            while (capacity < required) capacity <<= 1;

            Array.Resize(ref _colourClears, capacity);
            Array.Resize(ref _beginColour, capacity);
        }

        void RequireFramebuffer(string what)
        {
            if (_framebuffer.IsBound) return;

            throw new InvalidOperationException(
                what + " needs a framebuffer bound on the native Metal backend, and this recording has none. A "
                + "render encoder is opened from a descriptor naming the bound framebuffer's own attachment "
                + "textures, and there is no default target to fall back to: Begin resets the bound framebuffer, "
                + "because a fresh MTLCommandBuffer holds none.");
        }

        static void RequireSingleScissor(uint index)
        {
            if (index == 0) return;

            throw new ArgumentOutOfRangeException(nameof(index), index,
                "The native Metal backend sets ONE scissor rectangle, at index 0. The seam carries an output "
                + "index because Veldrid models one scissor per colour target, nothing in the engine passes a "
                + "non-zero one, and Metal's own rectangle is per ENCODER rather than per attachment, so "
                + "honouring an index would mean inventing a mapping no shipped renderer asks for. It is refused "
                + "by name rather than ignored, which is what both native sibling backends do with the same "
                + "index for the same reason.");
        }

        /// <summary>One attachment's pending clear: whether it is owed one and the value it folds into
        /// <c>loadAction</c>.</summary>
        readonly record struct PendingClear(bool Pending, Color Value);
    }
}
