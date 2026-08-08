using System;
using System.Globalization;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DEFERRED BEGIN, DECISIONS V-A1 TO V-A6 (section 7), and nothing else. It decides WHEN a render pass
    /// instance opens, what each attachment's <c>loadOp</c> is when it does, when a clear folds and when it costs
    /// a <c>vkCmdClearAttachments</c>, when the instance must close, and whether a framebuffer bind owes a
    /// viewport and a scissor. Every one of those is a decision that can be WRONG, and every one of them runs
    /// under a plain <c>[Fact]</c> on a machine with no Vulkan loader, because the six calls it makes go through
    /// <see cref="IVulkanRenderApi"/>.
    ///
    /// <list type="number">
    /// <item><description><b>The state is three things:</b> the bound framebuffer, a pending clear value per
    /// attachment, and whether rendering is currently begun.</description></item>
    /// <item><description><b><see cref="SetFramebuffer"/>.</b> A rebind of the framebuffer already bound does
    /// NOTHING AT ALL. A change ends any open instance, flushes the outgoing framebuffer's pending clears if no
    /// draw consumed them, records the new one, drops the pending array, and marks the viewport and the scissor
    /// for emission.</description></item>
    /// <item><description><b><see cref="ClearColourTarget"/> and <see cref="ClearDepthStencil"/>.</b> Before the
    /// instance opens, the value is stored as pending and becomes <c>loadOp = CLEAR</c> (V-A2). After it has
    /// opened, it is a <c>vkCmdClearAttachments</c> immediately, which is what the incumbent does in the same
    /// situation.</description></item>
    /// <item><description><b><see cref="PrepareDraw"/>, which the first draw of a pass calls.</b> Begin the
    /// instance with <c>loadOp = CLEAR</c> and the pending value per attachment that has one, <c>LOAD</c> for the
    /// rest and <c>storeOp = STORE</c> always (V-A6), then emit the viewport and the scissor if
    /// marked.</description></item>
    /// <item><description><b><see cref="EndRendering"/>, which <c>End</c> and every command illegal inside a
    /// render pass instance call (V-A4).</b> Close the instance, flushing pending clears through a begin and end
    /// pair first if there were any and no draw came. ONE invariant, ONE helper, and the callers that arrive in
    /// rows 13 and 15 call this rather than reimplementing it.</description></item>
    /// </list>
    ///
    /// <para><b>THE CLEAR-ONLY PASS IS REPRODUCED DELIBERATELY, NOT INHERITED BY ACCIDENT (V-A3).</b>
    /// <c>SetFramebuffer</c> plus a clear plus <c>End</c> with no draw between them must still clear, because the
    /// incumbent forces exactly that at two sites and a golden depends on it. Under a deferred begin that is a
    /// begin and end pair with no draws, and it is the ONE place the deferral needs an explicit flush rather than
    /// falling out of the schedule. It needs no "did a draw happen" flag to detect: a begin CONSUMES the pending
    /// array, so a pending clear still sitting there at the end of a pass is itself the proof that no draw
    /// came.</para>
    ///
    /// <para><b>THE FRAMEBUFFER-CHANGE GUARD WRAPS THE WHOLE OF <see cref="SetFramebuffer"/>, WHICH IS WHAT
    /// VELDRID'S BASE CLASS DOES (V-A5).</b> There is no <c>SetViewport</c> on the seam at all: the engine gets a
    /// viewport because <c>CommandList.SetFramebuffer</c> auto-calls <c>SetFullViewports</c> and
    /// <c>SetFullScissorRects</c>, and the whole body sits inside an <c>if (_framebuffer != fb)</c> identity
    /// guard. BOTH halves have to be reproduced. A backend that does not emit rasterises nothing. A backend that
    /// emits UNCONDITIONALLY diverges on the shipped sequence <c>SetFramebuffer(fb)</c>,
    /// <c>SetScissorRect(...)</c>, draw, <c>SetFramebuffer(fb)</c>, draw, where the second bind silently restores
    /// the full scissor and the second draw renders outside the intended rectangle. That is golden-visible, and
    /// phase 2's first spec froze the wrong behaviour into its tally test, which would have made the test certify
    /// the bug.</para>
    ///
    /// <para><b>AND THE MARKED VIEWPORT AND SCISSOR ARE VALUES RATHER THAN A "RE-EMIT THE FULL ONE" FLAG</b>,
    /// which is the subtle half of the same rule. Deferring the emission to the first draw and then emitting the
    /// FULL scissor there would clobber a rectangle the caller set in between, reintroducing the divergence from
    /// the other direction. So a framebuffer change SETS the pending scissor to the full extent and
    /// <see cref="SetScissorRect"/> overwrites it, and what the draw emits is whatever the last writer left.
    /// Repeated writes between two draws collapse to one emission, which is the same reason rule 6 of the bind
    /// schedule collapses repeated marks.</para>
    ///
    /// <para><b>ONE VIEWPORT AND ONE SCISSOR, AT INDEX 0, AND A NON-ZERO INDEX IS REFUSED.</b> The seam's
    /// <c>SetScissorRect</c> carries an output index because Veldrid models one scissor per colour target, and the
    /// native Direct3D 11 backend refuses a non-zero one for the same reason this does: nothing in the engine
    /// passes one, a Vulkan viewport is not per-attachment in the first place, and honouring an index would mean
    /// enabling <c>multiViewport</c> and matching the pipeline's viewport count to the attachment count for a
    /// shape no shipped renderer has. A refusal by name beats a silently ignored index.</para>
    ///
    /// <para><b>NOTHING HERE IS SYNCHRONISED</b>, on the same grounds as the list that owns it: one list records
    /// on one thread at a time and this schedule is that list's alone.</para>
    /// </summary>
    internal sealed class VulkanRenderingSchedule
    {
        readonly IVulkanRenderApi _api;

        // GROWN TO THE WIDEST FRAMEBUFFER EVER BOUND rather than reallocated per bind, and cleared rather than
        // replaced on a change, so a frame that alternates between two framebuffers allocates nothing.
        PendingClear[] _colourClears = new PendingClear[4];
        VulkanColourAttachment[] _beginColour = new VulkanColourAttachment[4];

        VulkanBoundFramebuffer _framebuffer;
        VulkanViewportRect _viewport;
        VulkanScissorRect _scissor;

        float _depthClear;
        bool _depthClearPending;
        bool _rendering;
        bool _viewportDirty;
        bool _scissorDirty;

        /// <param name="api">The six native rendering calls. Real on a device, a recording fake in the device-free
        /// tests.</param>
        internal VulkanRenderingSchedule(IVulkanRenderApi api)
        {
            ArgumentNullException.ThrowIfNull(api);

            _api = api;
        }

        /// <summary>Whether a render pass instance is currently open, which is the one piece of state that decides
        /// whether a clear folds or is issued.</summary>
        internal bool IsRendering => _rendering;

        /// <summary>The bound framebuffer as plain data, default when none is bound.</summary>
        internal VulkanBoundFramebuffer BoundFramebuffer => _framebuffer;

        /// <summary>Whether any attachment is owed a <c>loadOp = CLEAR</c>. A begin consumes these, so this
        /// reading true at the end of a pass is exactly the clear-only case (V-A3).</summary>
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

        /// <summary>The viewport a draw would emit, whether or not it is owed one. Its height is NEGATIVE
        /// (V-A5).</summary>
        internal VulkanViewportRect Viewport => _viewport;

        /// <summary>The scissor a draw would emit.</summary>
        internal VulkanScissorRect Scissor => _scissor;

        /// <summary>Whether the next draw owes a <c>vkCmdSetViewport</c>.</summary>
        internal bool ViewportDirty => _viewportDirty;

        /// <summary>Whether the next draw owes a <c>vkCmdSetScissor</c>.</summary>
        internal bool ScissorDirty => _scissorDirty;

        /// <summary>
        /// THE FRAMEBUFFER BIND, WHOLE-METHOD IDENTITY GUARD AND ALL. See the type remarks for why the guard
        /// covers everything rather than the viewport alone.
        /// </summary>
        /// <param name="commandBuffer">The buffer being recorded into.</param>
        /// <param name="framebuffer">The incoming framebuffer as plain data.</param>
        internal void SetFramebuffer(ulong commandBuffer, in VulkanBoundFramebuffer framebuffer)
        {
            if (framebuffer.Id == _framebuffer.Id) return;

            // THE OUTGOING FRAMEBUFFER'S PASS CLOSES FIRST, clear-only flush included, which is the same helper
            // End uses. A begin against the incoming attachments with the outgoing instance still open would be
            // two open instances on one buffer, which the driver refuses.
            EndRendering(commandBuffer);

            _framebuffer = framebuffer;

            EnsureColourCapacity(framebuffer.ColourCount);
            Array.Clear(_colourClears, 0, _colourClears.Length);
            _depthClearPending = false;

            _viewport = VulkanViewportRect.ForFramebuffer(framebuffer.Width, framebuffer.Height);
            _scissor = VulkanScissorRect.ForFramebuffer(framebuffer.Width, framebuffer.Height);
            _viewportDirty = true;
            _scissorDirty = true;
        }

        /// <summary>
        /// A COLOUR CLEAR, WHICH FOLDS OR ISSUES DEPENDING ON WHETHER THE PASS IS ALREADY OPEN (V-A2).
        /// </summary>
        /// <exception cref="InvalidOperationException">No framebuffer is bound.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The framebuffer has no colour attachment at that
        /// index.</exception>
        internal void ClearColourTarget(ulong commandBuffer, uint index, Color rgba)
        {
            RequireFramebuffer("Clearing a colour target");

            if (index >= (uint)_framebuffer.ColourCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "The bound native Vulkan framebuffer has "
                    + _framebuffer.ColourCount.ToString(CultureInfo.InvariantCulture)
                    + " colour attachment(s), so there is nothing at that index to clear. A colour attachment "
                    + "index is also its shader output location, so a pass clearing one it does not have is "
                    + "writing to a location its own pipeline cannot declare.");
            }

            if (_rendering)
            {
                _api.ClearColourAttachment(commandBuffer, index, rgba, _framebuffer.Width, _framebuffer.Height);
                return;
            }

            _colourClears[index] = new PendingClear(Pending: true, rgba);
        }

        /// <summary>
        /// THE DEPTH CLEAR, same fold-or-issue rule. The stencil plane goes with it at zero on a combined format,
        /// for the reason <see cref="VulkanDepthAttachment"/> gives.
        /// </summary>
        /// <exception cref="InvalidOperationException">No framebuffer is bound, or the bound one declares no depth
        /// attachment.</exception>
        internal void ClearDepthStencil(ulong commandBuffer, float depth)
        {
            RequireFramebuffer("Clearing the depth attachment");

            if (!_framebuffer.HasDepth)
            {
                throw new InvalidOperationException(
                    "The bound native Vulkan framebuffer declares no depth attachment, so there is nothing to "
                    + "clear. A framebuffer's attachments are fixed at creation, so this is the pass binding the "
                    + "wrong target rather than a depth attachment that failed to arrive.");
            }

            if (_rendering)
            {
                _api.ClearDepthAttachment(commandBuffer, depth, StencilPlane, _framebuffer.Width,
                    _framebuffer.Height);
                return;
            }

            _depthClear = depth;
            _depthClearPending = true;
        }

        /// <summary>
        /// An explicit scissor rectangle, which overwrites whatever the last writer left and is emitted by the
        /// next draw. See the type remarks for why this is a value rather than an immediate call.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not 0.</exception>
        internal void SetScissorRect(uint index, uint x, uint y, uint width, uint height)
        {
            RequireSingleScissor(index);

            _scissor = new VulkanScissorRect(checked((int)x), checked((int)y), width, height);
            _scissorDirty = true;
        }

        /// <summary>Restore the scissor to the bound framebuffer's full extent, which is what a framebuffer change
        /// applies and what this restores after an explicit rectangle.</summary>
        /// <exception cref="InvalidOperationException">No framebuffer is bound.</exception>
        internal void SetFullScissorRects()
        {
            RequireFramebuffer("Resetting the scissor rects");

            _scissor = VulkanScissorRect.ForFramebuffer(_framebuffer.Width, _framebuffer.Height);
            _scissorDirty = true;
        }

        /// <summary>
        /// THE PRE-DRAW HOOK: open the render pass instance if it is not open, then emit whatever dynamic state is
        /// owed. Row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525) calls this FIRST in <c>Draw</c> and
        /// <c>DrawIndexed</c>, before the bind flush and the vertex binds, then issues.
        /// <para>
        /// THE ORDER INSIDE IS FIXED. The instance opens before the viewport and the scissor go out, which is the
        /// order section 7.1 states, and it is the order that keeps the attachment transitions row 14
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524) adds at the begin ahead of every command that
        /// depends on them.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">No framebuffer is bound.</exception>
        internal void PrepareDraw(ulong commandBuffer)
        {
            RequireFramebuffer("Drawing");

            if (!_rendering) BeginRendering(commandBuffer);

            if (_viewportDirty)
            {
                _api.SetViewport(commandBuffer, in _viewport);
                _viewportDirty = false;
            }

            if (_scissorDirty)
            {
                _api.SetScissor(commandBuffer, in _scissor);
                _scissorDirty = false;
            }
        }

        /// <summary>
        /// THE END-BEFORE-ANYTHING-ILLEGAL INVARIANT, AS ONE HELPER (V-A4). <c>End</c> calls it, and so does every
        /// command that may not appear inside a render pass instance: a dispatch, a resolve, a copy and a mip
        /// generation. Those callers arrive in rows 13 and 15 and call this rather than writing the rule a second
        /// time, which is what "one invariant, one helper, one device-free test" means.
        /// <para>
        /// IT IS SAFE TO CALL WHEN NOTHING IS OPEN AND NOTHING IS PENDING, and does nothing then, so a caller
        /// never has to ask first.
        /// </para>
        /// </summary>
        internal void EndRendering(ulong commandBuffer)
        {
            // THE CLEAR-ONLY FLUSH (V-A3). A begin consumes the pending array, so pending clears here mean the
            // pass ended without a draw and the begin-and-end pair below is the only thing that will ever apply
            // them.
            if (!_rendering && HasPendingClears) BeginRendering(commandBuffer);

            if (!_rendering) return;

            _api.EndRendering(commandBuffer);
            _rendering = false;
        }

        /// <summary>
        /// FORGET EVERYTHING, which is what a fresh <c>VkCommandBuffer</c> holds: no framebuffer, no open
        /// instance, no dynamic state and no pending clears. Called from <c>VulkanCommandList.Begin</c>, between
        /// the native begin and the recording flag, for the reason
        /// <see cref="VulkanBindRecords.Reset"/> is called there.
        /// <para>
        /// THE PENDING CLEARS GO TOO, and dropping them is correct rather than lossy: they belong to a recording
        /// that was discarded, and a <c>Begin</c> discards a recording by contract.
        /// </para>
        /// </summary>
        internal void Reset()
        {
            _framebuffer = default;
            _rendering = false;
            _depthClearPending = false;
            _viewportDirty = false;
            _scissorDirty = false;
            _viewport = default;
            _scissor = default;
            Array.Clear(_colourClears, 0, _colourClears.Length);
        }

        // Whether the depth format carries a stencil plane, which decides whether a begin names a stencil
        // attachment and whether a mid-pass clear names the stencil aspect.
        bool StencilPlane => VulkanFormats.IsStencilFormat(_framebuffer.Depth.Format);

        // THE BEGIN ITSELF: one loadOp per attachment, storeOp = STORE always (V-A6), and the pending array
        // consumed. Reached from the first draw of a pass and from the clear-only flush, and from nowhere else.
        void BeginRendering(ulong commandBuffer)
        {
            ReadOnlySpan<VulkanAttachment> colour = _framebuffer.ColourAttachments;
            EnsureColourCapacity(colour.Length);

            for (int i = 0; i < colour.Length; i++)
            {
                PendingClear pending = _colourClears[i];
                _beginColour[i] = new VulkanColourAttachment(
                    colour[i].View,
                    pending.Pending ? VulkanLoadOp.Clear : VulkanLoadOp.Load,
                    pending.Value);
            }

            VulkanDepthAttachment? depth = _framebuffer.HasDepth
                ? new VulkanDepthAttachment(
                    _framebuffer.Depth.View,
                    _depthClearPending ? VulkanLoadOp.Clear : VulkanLoadOp.Load,
                    _depthClear,
                    StencilPlane)
                : null;

            // ROW 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/524) TRANSITIONS EVERY ATTACHMENT INTO ITS
            // ATTACHMENT LAYOUT HERE, immediately before the begin below, which is the point section 10.3's table
            // names. It has to be before: a barrier recorded inside an open render pass instance is a different
            // and much narrower call than the one that table describes. The attachments carry their VkImage for
            // exactly that, and nothing in this row reads it.

            _api.BeginRendering(commandBuffer, _framebuffer.Width, _framebuffer.Height,
                _beginColour.AsSpan(0, colour.Length), depth);

            _rendering = true;

            // CONSUMED, which is what makes a pending clear at the end of a pass mean "no draw came" with no flag
            // to keep in step.
            Array.Clear(_colourClears, 0, _colourClears.Length);
            _depthClearPending = false;
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
                what + " needs a framebuffer bound on the native Vulkan backend, and this recording has none. "
                + "A render pass instance is opened from the bound framebuffer's own attachment views, and there "
                + "is no default target to fall back to: Begin resets the bound framebuffer, because a fresh "
                + "VkCommandBuffer holds none.");
        }

        static void RequireSingleScissor(uint index)
        {
            if (index == 0) return;

            throw new ArgumentOutOfRangeException(nameof(index), index,
                "The native Vulkan backend sets ONE scissor rectangle, at index 0. The seam carries an output "
                + "index because Veldrid models one scissor per colour target, nothing in the engine passes a "
                + "non-zero one, and honouring it would mean enabling multiViewport and matching every pipeline's "
                + "viewport count to its attachment count for a shape no shipped renderer has. It is refused by "
                + "name rather than ignored, which is what the native Direct3D 11 backend does with the same "
                + "index for the same reason.");
        }

        /// <summary>One attachment's pending clear: whether it is owed one and the value it folds into
        /// <c>loadOp</c>.</summary>
        readonly record struct PendingClear(bool Pending, Color Value);
    }
}
