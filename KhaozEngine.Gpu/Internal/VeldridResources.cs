using System;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Shared liveness token between a device and every resource wrapper it created. Flipped once at
    /// device destruction (inside the lifecycle gate) and read lock-free by wrapper Dispose paths, so a wrapper
    /// disposed after its device no-ops instead of destroying a child object the dead device already freed (the
    /// Vulkan loader aborts a destroy call against a destroyed device).</summary>
    internal sealed class DeviceLiveness
    {
        public volatile bool Dead;
    }

    /// <summary>Wraps a Veldrid <see cref="DeviceBuffer"/>.</summary>
    internal sealed class VeldridGpuBuffer : IGpuBuffer
    {
        internal DeviceBuffer Buffer { get; }
        readonly DeviceLiveness _liveness;
        public uint SizeInBytes => Buffer.SizeInBytes;
        public VeldridGpuBuffer(DeviceLiveness liveness, DeviceBuffer buffer) { _liveness = liveness; Buffer = buffer; }
        public void Dispose() { if (!_liveness.Dead) Buffer.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Texture"/>.</summary>
    internal sealed class VeldridGpuTexture : IGpuTexture
    {
        internal Texture Texture { get; }
        readonly DeviceLiveness _liveness;
        public uint Width => Texture.Width;
        public uint Height => Texture.Height;
        public uint MipLevels => Texture.MipLevels;
        public uint SampleCount => VeldridMap.SampleCountToInt(Texture.SampleCount);
        public GpuPixelFormat Format => VeldridMap.FromVeldrid(Texture.Format);
        public VeldridGpuTexture(DeviceLiveness liveness, Texture texture) { _liveness = liveness; Texture = texture; }
        public void Dispose() { if (!_liveness.Dead) Texture.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Sampler"/>. <c>ownsSampler</c> is false for the device's shared
    /// point/linear samplers (owned by the device, not disposed here).</summary>
    internal sealed class VeldridGpuSampler : IGpuSampler
    {
        internal Sampler Sampler { get; }
        readonly DeviceLiveness _liveness;
        readonly bool _owns;
        public VeldridGpuSampler(DeviceLiveness liveness, Sampler sampler, bool ownsSampler = true)
        {
            _liveness = liveness; Sampler = sampler; _owns = ownsSampler;
        }
        public void Dispose() { if (_owns && !_liveness.Dead) Sampler.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Framebuffer"/>. <c>ownsFramebuffer</c> is false for the swapchain
    /// framebuffer (owned by the swapchain).</summary>
    internal sealed class VeldridGpuFramebuffer : IGpuFramebuffer
    {
        internal Framebuffer Framebuffer { get; }
        readonly DeviceLiveness _liveness;
        readonly bool _owns;
        public VeldridGpuFramebuffer(DeviceLiveness liveness, Framebuffer framebuffer, bool ownsFramebuffer = true)
        {
            _liveness = liveness; Framebuffer = framebuffer; _owns = ownsFramebuffer;
        }
        public GpuOutputDescription Outputs => VeldridMap.FromVeldrid(Framebuffer.OutputDescription);
        public uint Width => Framebuffer.Width;
        public uint Height => Framebuffer.Height;
        public void Dispose() { if (_owns && !_liveness.Dead) Framebuffer.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Pipeline"/>.</summary>
    internal sealed class VeldridGpuPipeline : IGpuPipeline
    {
        internal Pipeline Pipeline { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuPipeline(DeviceLiveness liveness, Pipeline pipeline) { _liveness = liveness; Pipeline = pipeline; }
        public void Dispose() { if (!_liveness.Dead) Pipeline.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="ResourceLayout"/>.</summary>
    internal sealed class VeldridGpuResourceLayout : IGpuResourceLayout
    {
        internal ResourceLayout Layout { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuResourceLayout(DeviceLiveness liveness, ResourceLayout layout) { _liveness = liveness; Layout = layout; }
        public void Dispose() { if (!_liveness.Dead) Layout.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="ResourceSet"/>.</summary>
    internal sealed class VeldridGpuResourceSet : IGpuResourceSet
    {
        internal ResourceSet Set { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuResourceSet(DeviceLiveness liveness, ResourceSet set) { _liveness = liveness; Set = set; }
        public void Dispose() { if (!_liveness.Dead) Set.Dispose(); }
    }

    /// <summary>Wraps the Veldrid <see cref="Shader"/>[] a SPIR-V cross-compile produces.</summary>
    internal sealed class VeldridGpuShaderSet : IGpuShaderSet
    {
        internal Shader[] Shaders { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuShaderSet(DeviceLiveness liveness, Shader[] shaders) { _liveness = liveness; Shaders = shaders; }
        public void Dispose() { if (_liveness.Dead) return; foreach (var s in Shaders) s.Dispose(); }
    }
}
