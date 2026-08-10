using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S FIVE PASS MEMBERS, resolving the framebuffer and handing every decision to
    /// <see cref="MetalRenderPassSchedule"/>. Work-breakdown row 12
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/578).
    ///
    /// <para><b>THE DECISIONS ARE NOT HERE ON PURPOSE, which is the same split <c>MetalCommandList.Uploads.cs</c>
    /// makes for the same reason.</b> When a pass opens, which attachment a clear folds onto, whether a
    /// framebuffer bind owes a viewport, and whether a clear-only pass still clears are all things that can be
    /// wrong in ways a golden sees late and a device-free test sees immediately, and none of them needs an
    /// <c>MTLDevice</c>. What is left here is the two things that DO need one: turning an
    /// <see cref="IGpuFramebuffer"/> into this backend's own record, and the recording state.</para>
    ///
    /// <para><b>A SEPARATE PARTIAL</b> because the lifecycle in <c>MetalCommandList.cs</c> is the part every later
    /// row reads before adding to it, and the design's own KESIZE warning for this phase is that the incumbent's
    /// <c>MTLCommandList.cs</c> is 1163 lines against an 800-line cap.</para>
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        /// <summary>
        /// THE DEFERRED BEGIN AND EVERY DECISION IN IT. Exposed because rows 11, 13 and 14 drive their own
        /// commands through it: row 11 tells it the bound pipeline's <c>ScissorTestEnabled</c>, and row 14 opens
        /// the pass through <see cref="MetalRenderPassSchedule.PrepareDraw"/> before row 13 flushes binds into
        /// the encoder it returns.
        /// </summary>
        internal MetalRenderPassSchedule Passes => _passes;

        /// <inheritdoc/>
        /// <remarks>
        /// The framebuffer is flattened to plain data at the BIND rather than read once at creation, which is
        /// what row 15's swapchain framebuffer needs: its colour attachment is the drawable's texture and moves
        /// on every acquire, or is the device-owned orphan target when the drawable came back nil (M-W5). An
        /// ordinary framebuffer answers the same record every time and pays nothing for the indirection.
        /// </remarks>
        public void SetFramebuffer(IGpuFramebuffer fb)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(fb);

            IMetalBoundFramebufferSource source = MetalFramebuffer.Require(fb, nameof(SetFramebuffer));
            RequireRecording("Binding a framebuffer");

            MetalBoundFramebuffer bound = source.AsBound;
            _passes.SetFramebuffer(in bound);
        }

        /// <inheritdoc/>
        /// <remarks>M-A2: it lands on the attachment this call NAMES, which is the one index the incumbent gets
        /// wrong and the one deliberate rendering change this phase spends on the reference golden family.
        /// </remarks>
        public void ClearColorTarget(uint index, Color rgba)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording("Clearing a colour target");

            _passes.ClearColourTarget(index, rgba);
        }

        /// <inheritdoc/>
        /// <remarks>The stencil plane clears to 0 with it on a combined format, which is the incumbent's own
        /// behaviour and what keeps the plane defined (M-A4's reasoning applied to a load).</remarks>
        public void ClearDepthStencil(float depth)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording("Clearing the depth attachment");

            _passes.ClearDepthStencil(depth);
        }

        /// <inheritdoc/>
        /// <remarks>Recorded as a value and emitted by the next draw whose pipeline has the seam's
        /// <c>ScissorTestEnabled</c> set (M-A6). A non-zero <paramref name="index"/> is refused by name.</remarks>
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording("Setting a scissor rect");

            _passes.SetScissorRect(index, x, y, w, h);
        }

        /// <inheritdoc/>
        public void SetFullScissorRects()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireRecording("Resetting the scissor rects");

            _passes.SetFullScissorRects();
        }

        // The recording guard, once, for the five members above. It says what a record-time pass command IS,
        // which is the thing a caller who reaches it has got wrong: these land at the point in the command
        // stream where they were recorded, and there is no stream yet.
        void RequireRecording(string what)
        {
            if (_recording) return;

            throw new InvalidOperationException(
                what + " needs a native Metal command list that is recording, and this one is not. Call Begin "
                + "first. Every pass command is encoder state or a decision about the encoder this recording "
                + "will open, and a list with no command buffer has nothing to open one on.");
        }
    }
}
