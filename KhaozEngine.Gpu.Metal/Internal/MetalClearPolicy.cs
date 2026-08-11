namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// WHICH ATTACHMENT A COLOUR CLEAR LANDS ON, which is decision M-A2. The backend ships
    /// <see cref="PerAttachment"/> unconditionally: <c>KE_METAL_CLEAR</c> is gone, and nothing in production
    /// reads or selects the other position any more.
    /// </summary>
    internal enum MetalClearMode
    {
        /// <summary>
        /// THE SHIPPED POSITION: a clear lands on the attachment the caller NAMED. One index, and it is the
        /// whole of M-A2.
        /// </summary>
        PerAttachment = 0,

        /// <summary>
        /// THE INCUMBENT'S DEFECT, REPRODUCED EXACTLY: every clear lands on attachment 0 whatever index the
        /// caller passed, so a framebuffer with more than one colour target never clears the rest at all.
        /// TEST-ONLY NOW, and it is kept because it is the only instrument that can tell the fix from the
        /// collapse. See the type remarks on <see cref="MetalClearPolicy"/>.
        /// </summary>
        Attachment0 = 1,
    }

    /// <summary>
    /// DECISION M-A2, AND ITS SWITCH IS RETIRED. <c>KE_METAL_CLEAR=attachment0</c> existed to put a run back on
    /// the incumbent's collapse for gate 1's A/B, and gate 1 read GREEN on 2026-08-11 (run 31464944222, commit
    /// b4b46fcf) with MM2 resolved, so the environment read, the once-per-process memo, the parse and the
    /// unrecognized-value WARN are all gone. What is left is one substitution over an explicit mode.
    ///
    /// <para><b>WHAT THE INCUMBENT DOES, AND WHY THE FIX WAS NOT AN INVISIBLE CORRECTION.</b> The Veldrid Metal
    /// backend writes every clear into <c>colorAttachments[0]</c>, so a framebuffer with more than one colour
    /// target clears only its first.
    /// <c>KhaozEngine.Render3D/Rendering/ModelRenderer.BeginModelPass</c> clears attachments 0, 1 and 2 of
    /// <c>ModelFB</c>, so this IS a deliberate rendering change on the fleet's reference golden family. 2.4 is
    /// the argument for making it: what those two attachments load under the incumbent is a freshly created
    /// <c>StorageModePrivate</c> texture nothing has written, which means the OLD behaviour was the unstable one
    /// and the committed goldens were baked reading it.</para>
    ///
    /// <para><b>THE ENUM OUTLIVES THE SWITCH BECAUSE THE GOLDEN FAMILY CANNOT SEE THIS CHANGE, AND ONE TEST
    /// CAN.</b> The A/B was taken on an M2 Max and the golden suite passed 31 of 31 in BOTH positions, so no
    /// golden discriminates the fix from the collapse. <c>MetalRenderPassGpuTests</c>'s negative arm does: it
    /// clears two attachments, reads the second one back as a texel, and fails under
    /// <see cref="MetalClearMode.Attachment0"/> exactly where it passes under
    /// <see cref="MetalClearMode.PerAttachment"/>. So the position stays reachable as a CONSTRUCTOR VALUE on
    /// the recording types (never from the environment and never from a device default), which keeps the
    /// discriminating instrument and costs production one comparison that is always the identity.</para>
    /// </summary>
    internal static class MetalClearPolicy
    {
        /// <summary>
        /// WHERE A CLEAR OF <paramref name="requestedIndex"/> ACTUALLY LANDS under <paramref name="mode"/>, and
        /// the single expression the whole of M-A2 is.
        /// <para>
        /// THE FOLD IS AT THE RECORD, NOT AT THE BEGIN, which is what made the two positions genuinely
        /// comparable and is what still makes the negative arm reproduce the defect rather than approximate it.
        /// Under <see cref="MetalClearMode.Attachment0"/> a clear of attachment 2 overwrites attachment 0's
        /// pending value exactly as the incumbent does, so a pass clearing three attachments to three colours
        /// ends up with the LAST of them on slot 0 and nothing on the rest. Folding at the begin instead would
        /// have to invent a rule for which of the three won.
        /// </para>
        /// </summary>
        /// <param name="mode">The policy this recording was built with. Every production recording is
        /// <see cref="MetalClearMode.PerAttachment"/>.</param>
        /// <param name="requestedIndex">The colour attachment index the caller named, already range-checked
        /// against the bound framebuffer.</param>
        internal static uint TargetIndex(MetalClearMode mode, uint requestedIndex)
            => mode == MetalClearMode.Attachment0 ? 0u : requestedIndex;
    }
}
