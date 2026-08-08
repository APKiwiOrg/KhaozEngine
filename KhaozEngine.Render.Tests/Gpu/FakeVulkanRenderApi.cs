using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>ONE <c>vkCmdBeginRendering</c> AS THE DRIVER WOULD HAVE RECEIVED IT: the render area and every
    /// attachment's load op and clear value, which is the whole of what a deferred begin decides.</summary>
    /// <param name="CommandBuffer">The buffer it was recorded into.</param>
    /// <param name="Width">Render-area width.</param>
    /// <param name="Height">Render-area height.</param>
    /// <param name="Colour">The colour attachments in order.</param>
    /// <param name="Depth">The depth attachment, or null.</param>
    internal readonly record struct VulkanRecordedBegin(
        ulong CommandBuffer, uint Width, uint Height, VulkanColourAttachment[] Colour,
        VulkanDepthAttachment? Depth);

    /// <summary>One <c>vkCmdClearAttachments</c>, which is what a clear that arrives after the pass has opened
    /// costs.</summary>
    /// <param name="Depth">True for the depth arm, false for a colour attachment.</param>
    /// <param name="Index">The colour attachment index, 0 on the depth arm.</param>
    /// <param name="Colour">The clear colour, default on the depth arm.</param>
    /// <param name="DepthValue">The clear depth, 0 on the colour arm.</param>
    /// <param name="Stencil">Whether the stencil plane cleared alongside the depth.</param>
    internal readonly record struct VulkanRecordedClear(
        bool Depth, uint Index, Color Colour, float DepthValue, bool Stencil);

    /// <summary>
    /// AN <see cref="IVulkanRenderApi"/> WITH NO DEVICE BEHIND IT: every call is recorded and nothing else
    /// happens, so the whole deferred-begin schedule (V-A1 to V-A6) runs under a plain <c>[Fact]</c> on a machine
    /// with no Vulkan loader.
    /// <para>
    /// IT RECORDS THE ARGUMENTS AND NOT ONLY THE COUNTS, because the two assertions this row owes are of both
    /// kinds. "Exactly one <c>vkCmdSetViewport</c> per framebuffer change and zero for a redundant rebind" is a
    /// count. "The emitted viewport height is NEGATIVE" and "the clear folded into <c>loadOp</c> rather than
    /// costing a call" are arguments, and a count alone cannot tell a correct begin from one that clears the wrong
    /// attachment.
    /// </para>
    /// <para>
    /// A CLASS RATHER THAN A STRUCT, unlike the <see cref="IVkCmdSink"/> fakes. Those are consumed through a
    /// generic constraint so the JIT monomorphizes them onto the per-draw path, and their state has to sit behind
    /// a reference for a copy to tally the same totals. Nothing on this seam scales with draw count, so it is
    /// consumed through the interface like every other native seam in this backend and there is no copy to worry
    /// about.
    /// </para>
    /// </summary>
    internal sealed class FakeVulkanRenderApi : IVulkanRenderApi
    {
        readonly List<VulkanRecordedBegin> _begins = new();
        readonly List<VulkanViewportRect> _viewports = new();
        readonly List<VulkanScissorRect> _scissors = new();
        readonly List<VulkanRecordedClear> _clears = new();
        readonly List<string> _trace = new();

        /// <summary>Every <c>vkCmdBeginRendering</c>, in order.</summary>
        internal IReadOnlyList<VulkanRecordedBegin> Begins => _begins;

        /// <summary>Every emitted viewport, in order. Its height is the line V-A5 is about.</summary>
        internal IReadOnlyList<VulkanViewportRect> Viewports => _viewports;

        /// <summary>Every emitted scissor, in order.</summary>
        internal IReadOnlyList<VulkanScissorRect> Scissors => _scissors;

        /// <summary>Every mid-pass <c>vkCmdClearAttachments</c>, in order. A deferred begin should make this list
        /// empty for the common case.</summary>
        internal IReadOnlyList<VulkanRecordedClear> Clears => _clears;

        /// <summary><c>vkCmdEndRendering</c> calls.</summary>
        internal int EndCount { get; private set; }

        /// <summary>Every call in order, as text, so a failing assertion can print what actually happened rather
        /// than only how many times.</summary>
        internal IReadOnlyList<string> Trace => _trace;

        /// <inheritdoc/>
        public void BeginRendering(ulong commandBuffer, uint width, uint height,
            ReadOnlySpan<VulkanColourAttachment> colour, VulkanDepthAttachment? depth)
        {
            _begins.Add(new VulkanRecordedBegin(commandBuffer, width, height, colour.ToArray(), depth));
            _trace.Add("BeginRendering(" + width.ToString(CultureInfo.InvariantCulture) + "x"
                + height.ToString(CultureInfo.InvariantCulture) + ",colour="
                + colour.Length.ToString(CultureInfo.InvariantCulture)
                + ",depth=" + (depth is null ? "none" : depth.Value.LoadOp.ToString()) + ")");
        }

        /// <inheritdoc/>
        public void EndRendering(ulong commandBuffer)
        {
            EndCount++;
            _trace.Add("EndRendering");
        }

        /// <inheritdoc/>
        public void SetViewport(ulong commandBuffer, in VulkanViewportRect viewport)
        {
            _viewports.Add(viewport);
            _trace.Add("SetViewport(y=" + viewport.Y.ToString(CultureInfo.InvariantCulture)
                + ",height=" + viewport.Height.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void SetScissor(ulong commandBuffer, in VulkanScissorRect scissor)
        {
            _scissors.Add(scissor);
            _trace.Add("SetScissor(" + scissor.X.ToString(CultureInfo.InvariantCulture) + ","
                + scissor.Y.ToString(CultureInfo.InvariantCulture) + ","
                + scissor.Width.ToString(CultureInfo.InvariantCulture) + ","
                + scissor.Height.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void ClearColourAttachment(ulong commandBuffer, uint index, Color rgba, uint width, uint height)
        {
            _clears.Add(new VulkanRecordedClear(Depth: false, index, rgba, 0f, Stencil: false));
            _trace.Add("ClearColourAttachment(" + index.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void ClearDepthAttachment(ulong commandBuffer, float depth, bool stencil, uint width, uint height)
        {
            _clears.Add(new VulkanRecordedClear(Depth: true, 0, default, depth, stencil));
            _trace.Add("ClearDepthAttachment(stencil=" + stencil.ToString() + ")");
        }
    }
}
