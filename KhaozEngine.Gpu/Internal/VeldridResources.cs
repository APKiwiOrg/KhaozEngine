using System;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Wraps a Veldrid <see cref="DeviceBuffer"/>.</summary>
    internal sealed class VeldridGpuBuffer : IGpuBuffer
    {
        internal DeviceBuffer Buffer { get; }
        public uint SizeInBytes => Buffer.SizeInBytes;
        public VeldridGpuBuffer(DeviceBuffer buffer) => Buffer = buffer;
        public void Dispose() => Buffer.Dispose();
    }

    /// <summary>Wraps a Veldrid <see cref="Texture"/>.</summary>
    internal sealed class VeldridGpuTexture : IGpuTexture
    {
        internal Texture Texture { get; }
        public uint Width => Texture.Width;
        public uint Height => Texture.Height;
        public uint MipLevels => Texture.MipLevels;
        public GpuPixelFormat Format => VeldridMap.FromVeldrid(Texture.Format);
        public VeldridGpuTexture(Texture texture) => Texture = texture;
        public void Dispose() => Texture.Dispose();
    }

    /// <summary>Wraps a Veldrid <see cref="Sampler"/>. <c>ownsSampler</c> is false for the device's shared
    /// point/linear samplers (owned by the device, not disposed here).</summary>
    internal sealed class VeldridGpuSampler : IGpuSampler
    {
        internal Sampler Sampler { get; }
        readonly bool _owns;
        public VeldridGpuSampler(Sampler sampler, bool ownsSampler = true) { Sampler = sampler; _owns = ownsSampler; }
        public void Dispose() { if (_owns) Sampler.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Framebuffer"/>. <c>ownsFramebuffer</c> is false for the swapchain
    /// framebuffer (owned by the swapchain).</summary>
    internal sealed class VeldridGpuFramebuffer : IGpuFramebuffer
    {
        internal Framebuffer Framebuffer { get; }
        readonly bool _owns;
        public VeldridGpuFramebuffer(Framebuffer framebuffer, bool ownsFramebuffer = true)
        {
            Framebuffer = framebuffer; _owns = ownsFramebuffer;
        }
        public GpuOutputDescription Outputs => VeldridMap.FromVeldrid(Framebuffer.OutputDescription);
        public uint Width => Framebuffer.Width;
        public uint Height => Framebuffer.Height;
        public void Dispose() { if (_owns) Framebuffer.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Pipeline"/>.</summary>
    internal sealed class VeldridGpuPipeline : IGpuPipeline
    {
        internal Pipeline Pipeline { get; }
        public VeldridGpuPipeline(Pipeline pipeline) => Pipeline = pipeline;
        public void Dispose() => Pipeline.Dispose();
    }

    /// <summary>Wraps a Veldrid <see cref="ResourceLayout"/>.</summary>
    internal sealed class VeldridGpuResourceLayout : IGpuResourceLayout
    {
        internal ResourceLayout Layout { get; }
        public VeldridGpuResourceLayout(ResourceLayout layout) => Layout = layout;
        public void Dispose() => Layout.Dispose();
    }

    /// <summary>Wraps a Veldrid <see cref="ResourceSet"/>.</summary>
    internal sealed class VeldridGpuResourceSet : IGpuResourceSet
    {
        internal ResourceSet Set { get; }
        public VeldridGpuResourceSet(ResourceSet set) => Set = set;
        public void Dispose() => Set.Dispose();
    }

    /// <summary>Wraps the Veldrid <see cref="Shader"/>[] a SPIR-V cross-compile produces.</summary>
    internal sealed class VeldridGpuShaderSet : IGpuShaderSet
    {
        internal Shader[] Shaders { get; }
        public VeldridGpuShaderSet(Shader[] shaders) => Shaders = shaders;
        public void Dispose() { foreach (var s in Shaders) s.Dispose(); }
    }
}
