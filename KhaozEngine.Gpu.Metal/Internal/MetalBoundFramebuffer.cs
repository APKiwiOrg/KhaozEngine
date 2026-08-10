using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE ATTACHMENT AS PLAIN DATA: the <c>MTLTexture</c> a descriptor names and the format that decides whether
    /// the depth arm carries a stencil plane.
    /// <para>
    /// THE HANDLE IS THE TEXTURE ITSELF AND NOT A VIEW, which is decision M-M10 arriving here rather than a
    /// simplification made in this row. <c>CreateFramebuffer</c> takes bare textures with no mip and no layer
    /// parameter, so an attachment can never be narrowed, and this package declares no view factory at all.
    /// </para>
    /// </summary>
    /// <param name="Texture">The <c>MTLTexture</c> handle, borrowed. Never <see cref="IntPtr.Zero"/> on a real
    /// attachment, which is what makes the handle its own "is there one" answer.</param>
    /// <param name="Format">The attachment's seam pixel format.</param>
    internal readonly record struct MetalAttachment(IntPtr Texture, GpuPixelFormat Format);

    /// <summary>
    /// EVERYTHING A RENDER PASS NEEDS FROM A FRAMEBUFFER, AS PLAIN DATA, which is what the recorder binds instead
    /// of the framebuffer object.
    ///
    /// <para><b>PLAIN DATA FOR THE SAME REASON THE VULKAN SIBLING USES IT, ARRIVED AT FROM A DIFFERENT
    /// DIRECTION.</b> There a field of the framebuffer type would put a view factory in the recorder's field
    /// graph and fail an unreachability walk. Here there IS no view factory anywhere in the package (M-M10), so
    /// the walk is not the reason: the reason is that the schedule's whole job is decisions, and a decision type
    /// holding an <see cref="IGpuFramebuffer"/> would be a decision type holding a disposable, an owner and a
    /// device. Handles, integers and enums keep <see cref="MetalRenderPassSchedule"/> constructible on a machine
    /// with no Metal at all, which is where every one of section 7.1's rules is asserted.</para>
    ///
    /// <para><b>THE COLOUR ARRAY IS HELD BY REFERENCE AND NEVER COPIED</b>, so a bind allocates nothing. The
    /// framebuffer writes it once at creation and a bind reads it.</para>
    ///
    /// <para><b>IDENTITY IS AN <see cref="Id"/> RATHER THAN A REFERENCE</b>, which is what M-A6's
    /// framebuffer-change guard compares. Veldrid's base <c>CommandList.SetFramebuffer</c> guards on
    /// <c>_framebuffer != fb</c> and the Direct3D 11 native backend reproduces that with
    /// <c>ReferenceEquals</c>, but plain data has no reference to compare, so each <see cref="MetalFramebuffer"/>
    /// takes a process-unique number at construction and carries it here. Zero means nothing is bound, which is
    /// what a fresh recording holds.</para>
    /// </summary>
    /// <param name="Id">The bound framebuffer's process-unique identity, or 0 for none.</param>
    /// <param name="Width">Width in pixels, which is the viewport and the full scissor.</param>
    /// <param name="Height">Height in pixels.</param>
    /// <param name="Colour">The colour attachments in order, or null when there are none (a depth-only shadow
    /// pass).</param>
    /// <param name="Depth">The depth attachment, default when the framebuffer declares none.</param>
    /// <param name="DepthHasStencil">Whether the depth format carries a stencil plane, decided once at
    /// framebuffer creation so no pass re-derives it.</param>
    internal readonly record struct MetalBoundFramebuffer(
        ulong Id, uint Width, uint Height, MetalAttachment[]? Colour, MetalAttachment Depth, bool DepthHasStencil)
    {
        /// <summary>Whether a framebuffer is bound at all. False for the state a fresh recording starts in.
        /// </summary>
        internal bool IsBound => Id != 0;

        /// <summary>How many colour attachments a descriptor names.</summary>
        internal int ColourCount => Colour?.Length ?? 0;

        /// <summary>Whether a descriptor names a depth attachment. A real attachment always has a texture, so the
        /// handle is the answer and there is no second flag to disagree with it.</summary>
        internal bool HasDepth => Depth.Texture != IntPtr.Zero;

        /// <summary>The colour attachments as a span, empty rather than null when there are none, so the descriptor
        /// path has one shape.</summary>
        internal ReadOnlySpan<MetalAttachment> ColourAttachments => Colour;
    }

    /// <summary>
    /// WHERE A BIND GETS ITS <see cref="MetalBoundFramebuffer"/> FROM, so the swapchain's own framebuffer goes
    /// down the identical path.
    /// <para>
    /// IT EXISTS FOR ROW 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/581) AND FOR M-W5. An ordinary
    /// framebuffer flattens once at construction and answers the same record forever. A swapchain framebuffer's
    /// colour attachment is the drawable's texture, which MOVES on every acquire, and when the drawable comes back
    /// nil it is the device-owned ORPHAN target instead. Both are a question asked at the bind rather than a value
    /// read at creation, which is why this is a property on an interface and not a field.
    /// </para>
    /// </summary>
    internal interface IMetalBoundFramebufferSource
    {
        /// <summary>The framebuffer as the recorder binds it, read at the moment of the bind.</summary>
        MetalBoundFramebuffer AsBound { get; }

        /// <summary>Whether this is the swapchain's framebuffer, which is what row 15's present path asks before
        /// deciding whether a frame has anything to present. False for every aggregate over engine
        /// textures.</summary>
        bool IsSwapchain { get; }
    }
}
