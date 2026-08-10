using System;
using System.Globalization;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// <see cref="IGpuFramebuffer"/> ON THE NATIVE METAL BACKEND: the attachment textures a pass renders into,
    /// gathered from engine textures. Work-breakdown row 12
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/578).
    ///
    /// <para><b>IT CREATES NO NATIVE OBJECT, AND ON THIS API THAT IS NOT EVEN A DECISION.</b> The Vulkan sibling
    /// had to reach dynamic rendering to delete its <c>VkRenderPass</c> and <c>VkFramebuffer</c> caches and the
    /// resize invalidation that came with them. Metal never had either: a pass is an
    /// <c>MTLRenderPassDescriptor</c> built per pass from the attachment textures themselves, so there is nothing
    /// here to create, nothing to cache and nothing to rebuild when the window changes size.</para>
    ///
    /// <para><b>SO IT OWNS NOTHING AND ITS DISPOSAL RELEASES NOTHING.</b> Every texture it aggregates belongs to
    /// whoever created it and outlives this object, which is the shape <c>D3D11Framebuffer</c> and
    /// <c>VulkanFramebuffer</c> both settled on. Here it also makes M-M10's eager-view bound real in the
    /// strongest available form: mip 0 and slice 0 are the whole story because <c>CreateFramebuffer</c> takes
    /// bare textures with no mip and no layer parameter, and the package declares no view factory to narrow one
    /// with even if it did.</para>
    ///
    /// <para><b>THE ATTACHMENTS ARE FLATTENED TO PLAIN DATA AT CONSTRUCTION</b>, into
    /// <see cref="MetalBoundFramebuffer"/>, and the textures are not held at all. A recorder binds the flattened
    /// record rather than this object, so <see cref="MetalRenderPassSchedule"/> stays constructible with no
    /// Metal anywhere, which is where every one of section 7.1's rules is asserted.</para>
    ///
    /// <para><b>IDENTITY IS A PROCESS-UNIQUE NUMBER</b>, taken here and carried in the flattened record, because
    /// M-A6's framebuffer-change guard compares plain data and has no reference to compare. Creation is
    /// free-threaded on this backend (M-W8), so the counter is interlocked.</para>
    ///
    /// <para><b>A STAGING TEXTURE IS REFUSED BY NAME, and it is the one refusal specific to this backend.</b> A
    /// <c>GpuTextureUsage.Staging</c> texture is an <c>MTLBuffer</c> here and not an <c>MTLTexture</c> at all
    /// (M-C5), so it carries no attachment handle. Without the check it would arrive at the descriptor as a nil
    /// texture, which Metal reports as a pass with no attachments rather than as a wrong argument.</para>
    /// </summary>
    internal sealed class MetalFramebuffer : IGpuFramebuffer, IMetalBoundFramebufferSource
    {
        // Starts at 0 and is PRE-incremented, so the first framebuffer is 1 and 0 is free to mean "nothing
        // bound", which is what a fresh recording holds.
        static long _nextId;

        /// <param name="depth">The depth attachment texture, or null.</param>
        /// <param name="colour">The colour attachment textures, in order. Possibly empty, which is the
        /// depth-only shadow pass.</param>
        internal MetalFramebuffer(MetalTexture? depth, MetalTexture[] colour)
        {
            ArgumentNullException.ThrowIfNull(colour);

            if (depth is null && colour.Length == 0)
            {
                throw new ArgumentException(
                    "A native Metal framebuffer needs at least one attachment. A pass with no target renders "
                    + "nowhere, and a render pass descriptor with no colour attachment and no depth attachment "
                    + "has no render area to derive.",
                    nameof(colour));
            }

            MetalTexture first = colour.Length > 0 ? colour[0] : depth!;
            Width = first.Width;
            Height = first.Height;

            var attachments = new MetalAttachment[colour.Length];
            var formats = new GpuPixelFormat[colour.Length];

            for (int i = 0; i < colour.Length; i++)
            {
                string what = "colour attachment " + i.ToString(CultureInfo.InvariantCulture);
                RequireMatching(first, colour[i], what);

                attachments[i] = new MetalAttachment(
                    RequireAttachable(colour[i], what, nameof(GpuTextureUsage.RenderTarget)), colour[i].Format);

                formats[i] = colour[i].Format;
            }

            MetalAttachment depthAttachment = default;
            bool depthHasStencil = false;
            if (depth is not null)
            {
                RequireMatching(first, depth, "depth attachment");
                depthAttachment = new MetalAttachment(
                    RequireAttachable(depth, "depth attachment", nameof(GpuTextureUsage.DepthStencil)),
                    depth.Format);

                depthHasStencil = MetalFormats.IsStencilFormat(depth.Format);
            }

            Outputs = new GpuOutputDescription(depth?.Format, formats)
                .WithSampleCount((int)first.SampleCount);

            AsBound = new MetalBoundFramebuffer(
                (ulong)Interlocked.Increment(ref _nextId), Width, Height, attachments, depthAttachment,
                depthHasStencil);
        }

        /// <inheritdoc/>
        public GpuOutputDescription Outputs { get; }

        /// <inheritdoc/>
        public uint Width { get; }

        /// <inheritdoc/>
        public uint Height { get; }

        /// <summary>
        /// EVERYTHING A BIND NEEDS FROM THIS FRAMEBUFFER, AS PLAIN DATA. Flattened once at construction and handed
        /// to the recorder, which never holds this object.
        /// </summary>
        internal MetalBoundFramebuffer AsBound { get; }

        /// <inheritdoc/>
        /// <remarks>Explicit, because the interface is internal and this property already exists under the same
        /// name. The recording path binds through the interface so row 15's swapchain framebuffer, whose
        /// attachment moves on every acquire, goes down the identical path.</remarks>
        MetalBoundFramebuffer IMetalBoundFramebufferSource.AsBound => AsBound;

        /// <inheritdoc/>
        /// <remarks>An aggregate over engine textures is never the swapchain's, whatever those textures are: the
        /// drawable is reachable only through the device's own swapchain framebuffer (row 15).</remarks>
        bool IMetalBoundFramebufferSource.IsSwapchain => false;

        /// <summary>This framebuffer's process-unique identity, which M-A6's framebuffer-change guard
        /// compares.</summary>
        internal ulong Id => AsBound.Id;

        /// <summary>True once disposed. Nothing native is released, because nothing native was made.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>
        /// Releases NOTHING, and that follows from the type remarks rather than being an omission: there is no
        /// render pass object to destroy and no framebuffer object to release, and every texture this aggregates
        /// belongs to whoever created it.
        /// </summary>
        public void Dispose() => IsDisposed = true;

        /// <summary>The framebuffer as a bind source, or a named refusal for one another backend made.</summary>
        internal static IMetalBoundFramebufferSource Require(IGpuFramebuffer? framebuffer, string what)
            => framebuffer as IMetalBoundFramebufferSource
                ?? throw new ArgumentException(
                    $"The framebuffer handed to {what} was not created by the native Metal backend, so it carries "
                    + "no MTLTexture to render into. Create it through the same IGpuDevice.Factory.",
                    nameof(framebuffer));

        static void RequireMatching(MetalTexture first, MetalTexture attachment, string what)
        {
            if (attachment.Width == first.Width && attachment.Height == first.Height
                && attachment.SampleCount == first.SampleCount)
            {
                return;
            }

            throw new ArgumentException(
                "Every native Metal framebuffer attachment must share one size and one sample count, because the "
                + "render area and the pipeline's sample count are both single values. The " + what + " is "
                + attachment.Width.ToString(CultureInfo.InvariantCulture) + "x"
                + attachment.Height.ToString(CultureInfo.InvariantCulture) + " at "
                + attachment.SampleCount.ToString(CultureInfo.InvariantCulture) + " samples against "
                + first.Width.ToString(CultureInfo.InvariantCulture) + "x"
                + first.Height.ToString(CultureInfo.InvariantCulture) + " at "
                + first.SampleCount.ToString(CultureInfo.InvariantCulture) + ".");
        }

        // The two ways an attachment can have no MTLTexture behind it, and both are USAGE mistakes rather than
        // backend ones. A staging texture is an MTLBuffer here and never a texture at all (M-C5), and a disposed
        // texture answers a nil handle by design. Either would reach the descriptor as nil, which Metal reports
        // as a pass with no attachments rather than as the wrong argument.
        static IntPtr RequireAttachable(MetalTexture texture, string what, string usage)
        {
            if (texture.IsStaging)
            {
                throw new ArgumentException(
                    "The " + what + " is a GpuTextureUsage.Staging texture, which on the native Metal backend is "
                    + "a Shared MTLBuffer carrying a software subresource layout and not an MTLTexture at all "
                    + "(M-C5). Nothing can render into one. Create the attachment with GpuTextureUsage." + usage
                    + " and copy into the staging texture afterwards.");
            }

            IntPtr handle = texture.Handle.Handle;
            if (handle != IntPtr.Zero) return handle;

            throw new ArgumentException(
                "The " + what + " has no MTLTexture to bind, which on this backend means the texture has already "
                + "been disposed: every non-staging texture is created with its native texture and holds it for "
                + "life, and there is no view factory anywhere in this package to make a second one with (M-M10).");
        }
    }
}
