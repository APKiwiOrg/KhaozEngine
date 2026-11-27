using System;
using System.Globalization;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreFramebuffer(CoreGpuDevice owner, khaoz_gpu_framebuffer* handle,
    uint width, uint height, GpuOutputDescription outputs)
    : CoreResource(owner, handle), IGpuFramebuffer
{
    public GpuOutputDescription Outputs { get; } = outputs;
    public uint Width { get; } = width;
    public uint Height { get; } = height;

    internal khaoz_gpu_framebuffer* Handle => (khaoz_gpu_framebuffer*)NativeHandle;

    internal static CoreFramebuffer Create(CoreGpuDevice device, IGpuTexture? depth, IGpuTexture[] colour)
    {
        ArgumentNullException.ThrowIfNull(colour);
        if (depth is null && colour.Length == 0)
            throw new GpuFramebufferException(GpuFramebufferRefusal.NoAttachments,
                "A framebuffer needs at least one attachment.", nameof(colour))
            {
                ColourAttachmentCount = colour.Length,
                HasDepth = false,
            };

        lock (device.Gate)
        {
            const string colourContext = "a native Vulkan framebuffer colour attachment";
            var depthTexture = depth is null ? null : RequireOwned<CoreTexture>(device, depth, "texture", "texture",
                "a native Vulkan framebuffer depth attachment");
            var first = colour.Length == 0 ? depthTexture! :
                RequireOwned<CoreTexture>(device, colour[0], "texture", "texture", colourContext);
            var formats = new GpuPixelFormat[colour.Length];
            var handles = new nint[colour.Length];
            for (int i = 0; i < colour.Length; i++)
            {
                var texture = RequireOwned<CoreTexture>(device, colour[i], "texture", "texture", colourContext);
                string attachment = "colour attachment " + i.ToString(CultureInfo.InvariantCulture);
                RequireAttachment(first, texture, attachment, GpuTextureUsage.RenderTarget);
                formats[i] = texture.Format;
                handles[i] = (nint)texture.Handle;
            }
            if (depthTexture is not null)
                RequireAttachment(first, depthTexture, "depth attachment", GpuTextureUsage.DepthStencil);

            var outputs = new GpuOutputDescription(depthTexture?.Format, formats).WithSampleCount((int)first.SampleCount);
            fixed (nint* colors = handles)
            {
                khaoz_gpu_framebuffer* framebuffer;
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_framebuffer_create(device.Handle,
                    depthTexture is null ? null : depthTexture.Handle, (khaoz_gpu_texture**)colors,
                    (uint)handles.Length, &framebuffer), nameof(IGpuResourceFactory.CreateFramebuffer));
                return new CoreFramebuffer(device, framebuffer, first.Width, first.Height, outputs);
            }
        }
    }

    private static void RequireAttachment(CoreTexture first, CoreTexture texture, string attachment,
        GpuTextureUsage required)
    {
        if (first.Width != texture.Width || first.Height != texture.Height || first.SampleCount != texture.SampleCount)
            throw new GpuFramebufferException(GpuFramebufferRefusal.AttachmentShapeMismatch,
                "Every framebuffer attachment must have the same dimensions and sample count.", null)
            {
                Attachment = attachment,
                Width = texture.Width,
                Height = texture.Height,
                SampleCount = texture.SampleCount,
                ExpectedWidth = first.Width,
                ExpectedHeight = first.Height,
                ExpectedSampleCount = first.SampleCount,
            };
        if ((texture.Usage & required) == 0)
            throw new GpuFramebufferException(texture.IsStaging ? GpuFramebufferRefusal.AttachmentNotRenderable
                    : GpuFramebufferRefusal.AttachmentViewMissing,
                "The framebuffer attachment lacks the required texture usage.", null)
            {
                Attachment = attachment,
                ActualUsage = texture.Usage,
                RequiredUsage = required,
                IsStaging = texture.IsStaging,
                NativeHandleKind = "VkImageView",
            };
    }

    protected override khaoz_result Release(void* value)
        => KhaozNative.khaoz_gpu_framebuffer_destroy((khaoz_gpu_framebuffer*)value);
}
