using System;
using System.Globalization;
using System.Threading;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuFramebuffer"/> on the native Vulkan backend: the attachment views a pass binds, gathered
    /// from engine textures. Work-breakdown row 12 (https://github.com/APKiwiOrg/KhaozEngine/issues/522).
    ///
    /// <para><b>THIS TYPE CREATES NO NATIVE OBJECT AT ALL, AND THAT IS THE HEADLINE OF DECISION V-A1.</b> There is
    /// no <c>VkFramebuffer</c> here and no <c>VkRenderPass</c> anywhere in this backend, so there is no cache for
    /// either and no invalidation of either on a resize. The incumbent creates three render passes per framebuffer
    /// with no cache and no dedup across framebuffers of identical format, one <c>VkFramebuffer</c> per swapchain
    /// image, and rebuilds all of it on every resize. Dynamic rendering deletes both caches and the invalidation
    /// problem with them: <c>IGpuFramebuffer.Outputs</c> is already <c>VkPipelineRenderingCreateInfo</c>'s input
    /// verbatim, and a render pass instance takes the attachment views themselves.</para>
    ///
    /// <para><b>SO IT OWNS NOTHING EITHER, and its disposal releases nothing.</b> Every view already exists on the
    /// texture, made at TEXTURE creation from the declared usage bits (V-M11), and a framebuffer is an aggregate
    /// of borrowed handles. The textures outlive it and are disposed by whoever created them. That is the same
    /// shape <c>D3D11Framebuffer</c> settled on for the same reason, and it is what makes the eager-view bound
    /// real: mip 0 and layer 0 are the whole story because <c>CreateFramebuffer</c> takes bare textures with no
    /// mip and no layer parameter.</para>
    ///
    /// <para><b>THE ATTACHMENTS ARE FLATTENED TO PLAIN DATA AT CONSTRUCTION</b>, into
    /// <see cref="VulkanBoundFramebuffer"/>, and the textures are not held at all. A recorder binds the flattened
    /// record rather than this object, because a field of this type would put the view factory in the recording
    /// type's field graph through the textures. See <see cref="VulkanBoundFramebuffer"/> for the full argument and
    /// for the row-10-to-row-11 precedent it follows.</para>
    ///
    /// <para><b>IDENTITY IS A PROCESS-UNIQUE NUMBER</b>, taken here and carried in the flattened record, because
    /// the framebuffer-change guard compares plain data and has no reference to compare. Creation is
    /// free-threaded on this backend (V-W8), so the counter is interlocked.</para>
    /// </summary>
    internal sealed class VulkanFramebuffer : IGpuFramebuffer
    {
        // Starts at 0 and is PRE-incremented, so the first framebuffer is 1 and 0 is free to mean "nothing bound".
        static long _nextId;

        /// <param name="depth">The depth attachment texture, or null.</param>
        /// <param name="colour">The colour attachment textures, in order. Possibly empty, which is the depth-only
        /// shadow pass.</param>
        internal VulkanFramebuffer(VulkanTexture? depth, VulkanTexture[] colour)
        {
            ArgumentNullException.ThrowIfNull(colour);

            if (depth is null && colour.Length == 0)
            {
                throw new ArgumentException(
                    "A native Vulkan framebuffer needs at least one attachment. A pass with no target renders "
                    + "nowhere, and vkCmdBeginRendering with no colour attachment and no depth attachment has no "
                    + "render area to derive.",
                    nameof(colour));
            }

            VulkanTexture first = colour.Length > 0 ? colour[0] : depth!;
            Width = first.Width;
            Height = first.Height;

            var attachments = new VulkanAttachment[colour.Length];
            var formats = new GpuPixelFormat[colour.Length];

            for (int i = 0; i < colour.Length; i++)
            {
                RequireMatching(first, colour[i], "colour attachment "
                    + i.ToString(CultureInfo.InvariantCulture));

                attachments[i] = new VulkanAttachment(
                    RequireView(colour[i], "colour attachment " + i.ToString(CultureInfo.InvariantCulture),
                        nameof(GpuTextureUsage.RenderTarget)),
                    colour[i].Image, colour[i].Format, DepthStencil: false, colour[i].Resting);

                formats[i] = colour[i].Format;
            }

            VulkanAttachment depthAttachment = default;
            if (depth is not null)
            {
                RequireMatching(first, depth, "depth attachment");
                depthAttachment = new VulkanAttachment(
                    RequireView(depth, "depth attachment", nameof(GpuTextureUsage.DepthStencil)),
                    depth.Image, depth.Format, DepthStencil: true, depth.Resting);
            }

            Outputs = new GpuOutputDescription(depth?.Format, formats)
                .WithSampleCount((int)first.SampleCount);

            AsBound = new VulkanBoundFramebuffer(
                (ulong)Interlocked.Increment(ref _nextId), Width, Height, attachments, depthAttachment);
        }

        /// <inheritdoc/>
        public GpuOutputDescription Outputs { get; }

        /// <inheritdoc/>
        public uint Width { get; }

        /// <inheritdoc/>
        public uint Height { get; }

        /// <summary>
        /// EVERYTHING A BIND NEEDS FROM THIS FRAMEBUFFER, AS PLAIN DATA. Flattened once at construction and
        /// handed to the recorder, which never holds this object. See <see cref="VulkanBoundFramebuffer"/>.
        /// </summary>
        internal VulkanBoundFramebuffer AsBound { get; }

        /// <summary>This framebuffer's process-unique identity, which the framebuffer-change guard
        /// compares.</summary>
        internal ulong Id => AsBound.Id;

        /// <summary>True once disposed. Nothing native is released, because nothing native was made. See the type
        /// remarks.</summary>
        internal bool IsDisposed { get; private set; }

        /// <summary>
        /// Releases NOTHING, and that follows from V-A1 plus V-M11 rather than being an omission: there is no
        /// <c>VkFramebuffer</c> to destroy and no <c>VkRenderPass</c> to release, and every view this aggregates
        /// belongs to the texture that made it.
        /// </summary>
        public void Dispose() => IsDisposed = true;

        /// <summary>The framebuffer as this backend's own, or a named refusal for one another backend
        /// made.</summary>
        internal static VulkanFramebuffer Require(IGpuFramebuffer? framebuffer, string what)
            => framebuffer as VulkanFramebuffer
                ?? throw new ArgumentException(
                    $"The framebuffer handed to {what} was not created by the native Vulkan backend, so it carries "
                    + "no VkImageView to render into. Create it through the same IGpuDevice.Factory.",
                    nameof(framebuffer));

        static void RequireMatching(VulkanTexture first, VulkanTexture attachment, string what)
        {
            if (attachment.Width == first.Width && attachment.Height == first.Height
                && attachment.SampleCount == first.SampleCount)
            {
                return;
            }

            throw new ArgumentException(
                "Every native Vulkan framebuffer attachment must share one size and one sample count, because "
                + "the render area and the pipeline's sample count are both single values. The "
                + what + " is "
                + attachment.Width.ToString(CultureInfo.InvariantCulture) + "x"
                + attachment.Height.ToString(CultureInfo.InvariantCulture) + " at "
                + attachment.SampleCount.ToString(CultureInfo.InvariantCulture) + " samples against "
                + first.Width.ToString(CultureInfo.InvariantCulture) + "x"
                + first.Height.ToString(CultureInfo.InvariantCulture) + " at "
                + first.SampleCount.ToString(CultureInfo.InvariantCulture) + ".");
        }

        // The one thing an attachment can be missing, and it is a USAGE mistake rather than a backend one: views
        // follow from the declared usage at creation (V-M11), so a texture that did not ask to be a target never
        // got one and no framebuffer can conjure it here.
        static ulong RequireView(VulkanTexture texture, string what, string usage)
        {
            if (texture.AttachmentView != 0) return texture.AttachmentView;

            throw new ArgumentException(
                "The " + what + " has no VkImageView to bind, because its texture was not created with "
                + "GpuTextureUsage." + usage + ". Every view a texture will ever need is created at TEXTURE "
                + "creation from its declared usage bits and none at a bind or a draw (V-M11), so there is no "
                + "view factory reachable from here to make one with. The texture is a "
                + texture.Describe() + ".");
        }
    }
}
