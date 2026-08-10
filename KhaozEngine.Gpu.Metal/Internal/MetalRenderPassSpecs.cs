using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// What an attachment does with its EXISTING contents when a render encoder opens, as THIS BACKEND's own
    /// value rather than the Objective-C enum. Under M-A1's deferred begin it is the whole of how a clear is
    /// expressed, which is why no clear COMMAND exists anywhere in this backend.
    /// <para>
    /// THERE IS NO <c>DontCare</c> ARM, deliberately, and the reason is the store side's (M-A4): a load of
    /// <c>DontCare</c> leaves the attachment undefined, undefined is not stable across runs, and the goldens
    /// require stability on the same device. An arm that cannot be chosen is better than one that can be chosen
    /// by accident. The interop enum HAS the member, because 0 is what a descriptor field holds before anything
    /// writes it and a reader checking a default needs the name.
    /// </para>
    /// </summary>
    internal enum MetalLoadAction
    {
        /// <summary>The attachment keeps what it already held. Every attachment with no pending clear, including
        /// one whose pass was split by a record-time blit and reopened.</summary>
        Load,

        /// <summary>The attachment is filled with the clear value beside it. What a clear recorded before the
        /// first draw of a pass folds into, on the attachment the CALLER named (M-A2).</summary>
        Clear,
    }

    /// <summary>
    /// What happens to an attachment's contents when the encoder ends.
    ///
    /// <para><b>ONLY <see cref="Store"/> IS EVER CHOSEN TODAY, AND UNLIKE THE VULKAN SIBLING THIS BACKEND CARRIES
    /// IT AS A VALUE RATHER THAN LEAVING IT IMPLICIT.</b> Phase 3's equivalent record deliberately has no store
    /// field, on the rule that a field which only ever holds one value is a field somebody eventually sets to the
    /// other one. That rule is right where nothing is queued to change, and here two things are: section 2.5
    /// records depth <see cref="DontCare"/> and the folded resolve as measured follow-ups with named consumers,
    /// each blocked by a stated argument (determinism for the first, golden attribution for the second) rather
    /// than by being bad ideas. So the choice travels in the plan, where a device-free test reads it and where a
    /// future change lands at a seam something can see, instead of being a constant inside the one method that
    /// makes native calls.</para>
    ///
    /// <para><b>AND M-A4 IS ABOUT THE NATIVE CALL, NOT ABOUT THIS ENUM.</b> The descriptor's own default is
    /// <c>DontCare</c>, which DISCARDS the attachment, so the store action is SET on every attachment rather than
    /// left alone. A plan carrying <see cref="Store"/> and an implementation that never sent it would render
    /// nothing at all, which is why the plural of this decision has a <c>[GpuFact]</c> readback behind it and not
    /// only a device-free row.</para>
    /// </summary>
    internal enum MetalStoreAction
    {
        /// <summary>The contents are written out. The only value this backend selects, for colour and for depth
        /// alike.</summary>
        Store,

        /// <summary>The contents are discarded. NEVER selected, and declared so 2.5's depth follow-up has a name
        /// to arrive under rather than needing this enum widened first.</summary>
        DontCare,
    }

    /// <summary>
    /// ONE COLOUR ATTACHMENT AS A BEGIN NAMES IT: the texture, what happens to its contents on the way in, the
    /// value it clears to when it clears, and what happens to them on the way out.
    /// </summary>
    /// <param name="Texture">The <c>MTLTexture</c> handle, borrowed from the framebuffer's own texture. Never
    /// <see cref="IntPtr.Zero"/> on a real attachment.</param>
    /// <param name="LoadAction">Load, or clear with <paramref name="ClearValue"/>.</param>
    /// <param name="ClearValue">The clear colour, meaningful only under <see cref="MetalLoadAction.Clear"/>. The
    /// engine's four floats in 0 to 1, which is already <c>MTLClearColor</c>'s own range.</param>
    /// <param name="StoreAction">What the end of the pass does with the result.</param>
    internal readonly record struct MetalColourAttachment(
        IntPtr Texture, MetalLoadAction LoadAction, Color ClearValue, MetalStoreAction StoreAction);

    /// <summary>
    /// THE DEPTH ATTACHMENT AS A BEGIN NAMES IT, plus the one thing the colour arm has no analogue for.
    /// <para>
    /// <paramref name="Stencil"/> DECIDES WHETHER THE DESCRIPTOR NAMES A STENCIL ATTACHMENT AT ALL, which is the
    /// incumbent's <c>FormatHelpers.IsStencilFormat</c> guard: naming one over a depth-only texture is a
    /// validation error under the debug layer M-T7 arms on every run. When it is named, the stencil plane clears
    /// to 0 alongside the depth, because the seam's <c>ClearDepthStencil</c> carries no stencil value and the
    /// incumbent clears it to 0. Leaving the plane out would leave it holding whatever the last pass wrote, which
    /// is the same undefined-is-not-stable case M-A4 rules on for the store action.
    /// </para>
    /// </summary>
    /// <param name="Texture">The depth <c>MTLTexture</c> handle, or <see cref="IntPtr.Zero"/> when the
    /// framebuffer declares no depth attachment. That handle IS the "is there one" answer, so there is no second
    /// flag to disagree with it.</param>
    /// <param name="LoadAction">Load, or clear to <paramref name="ClearDepth"/>.</param>
    /// <param name="ClearDepth">The depth value, meaningful only under <see cref="MetalLoadAction.Clear"/>.</param>
    /// <param name="StoreAction">What the end of the pass does with the result. <see cref="MetalStoreAction.Store"/>
    /// today, and the seat 2.5's tiler follow-up arrives at.</param>
    /// <param name="Stencil">Whether the depth format carries a stencil plane.</param>
    internal readonly record struct MetalDepthAttachment(
        IntPtr Texture, MetalLoadAction LoadAction, float ClearDepth, MetalStoreAction StoreAction, bool Stencil)
    {
        /// <summary>Whether a begin names a depth attachment at all.</summary>
        internal bool Present => Texture != IntPtr.Zero;
    }

    /// <summary>
    /// The viewport a draw would emit, as this backend's own value so section 7.3's assertions read plain
    /// numbers.
    /// <para>
    /// <see cref="Height"/> IS POSITIVE, and a reader arriving from phase 3 will look for the trick that is not
    /// here. Vulkan needed a negative viewport height to make its clip space match the engine's, and it was the
    /// single most consequential line in that design. Metal's clip space already matches, so
    /// <c>GpuCapabilities.ClipSpaceYInverted</c> is false and <c>GpuClip.Correct</c> is the identity.
    /// </para>
    /// </summary>
    /// <param name="X">Left edge in pixels.</param>
    /// <param name="Y">Top edge in pixels.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels, POSITIVE.</param>
    /// <param name="MinDepth">Near plane, 0 for every shipped pass.</param>
    /// <param name="MaxDepth">Far plane, 1.</param>
    internal readonly record struct MetalViewportRect(
        float X, float Y, float Width, float Height, float MinDepth, float MaxDepth)
    {
        /// <summary>
        /// The full-framebuffer viewport. This is the value Veldrid's base <c>CommandList.SetFramebuffer</c>
        /// auto-applies through <c>SetFullViewports</c>, which is the only reason the engine has a viewport at
        /// all: there is no <c>SetViewport</c> on the seam, so a backend that does not emit this rasterises
        /// nothing.
        /// </summary>
        internal static MetalViewportRect ForFramebuffer(uint width, uint height)
            => new(0f, 0f, width, height, 0f, 1f);
    }

    /// <summary>
    /// The scissor rectangle a draw would emit, for the other half of the same auto-applied pair.
    /// <para>
    /// WHETHER IT IS EMITTED AT ALL IS NOT A PROPERTY OF THIS VALUE. Metal has no scissor-test enable and the
    /// rectangle is always live, so the gate is the bound pipeline's <c>ScissorTestEnabled</c>, which is the SEAM's
    /// own rasterizer state. <see cref="MetalRenderPassSchedule"/> owns that decision.
    /// </para>
    /// </summary>
    /// <param name="X">Left edge in pixels.</param>
    /// <param name="Y">Top edge in pixels. NOT flipped: a scissor is a framebuffer-space rectangle with no clip
    /// space to correct for.</param>
    /// <param name="Width">Width in pixels.</param>
    /// <param name="Height">Height in pixels.</param>
    internal readonly record struct MetalScissorRect(uint X, uint Y, uint Width, uint Height)
    {
        /// <summary>The full-framebuffer rectangle, which a framebuffer change applies and which
        /// <c>SetFullScissorRects</c> restores after an explicit one.</summary>
        internal static MetalScissorRect ForFramebuffer(uint width, uint height) => new(0, 0, width, height);
    }
}
