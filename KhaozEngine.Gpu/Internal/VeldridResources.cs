using System;
using Veldrid;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>Wraps a Veldrid <see cref="DeviceBuffer"/>.</summary>
    internal sealed class VeldridGpuBuffer : IGpuBuffer
    {
        internal DeviceBuffer Buffer { get; }
        readonly DeviceLiveness _liveness;
        public uint SizeInBytes => Buffer.SizeInBytes;
        public VeldridGpuBuffer(DeviceLiveness liveness, DeviceBuffer buffer) { _liveness = liveness; Buffer = buffer; }
        public void Dispose() { if (_liveness.IsAlive) Buffer.Dispose(); }
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
        public void Dispose() { if (_liveness.IsAlive) Texture.Dispose(); }
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
        public void Dispose() { if (_owns && _liveness.IsAlive) Sampler.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Fence"/>. <see cref="Signaled"/> reads true once the device is dead:
    /// the fence is a question about outstanding GPU work, and a destroyed device has none, so a consumer polling
    /// a straggler fence at teardown gets "done" rather than a call into a freed device (same liveness contract as
    /// <c>VeldridGpuDevice.WaitForIdle</c>).</summary>
    internal sealed class VeldridGpuFence : IGpuFence
    {
        internal Fence Fence { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuFence(DeviceLiveness liveness, Fence fence) { _liveness = liveness; Fence = fence; }
        public bool Signaled => _liveness.IsDead || Fence.Signaled;
        public void Reset() { if (_liveness.IsAlive) Fence.Reset(); }
        public void Dispose() { if (_liveness.IsAlive) Fence.Dispose(); }
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
        public void Dispose() { if (_owns && _liveness.IsAlive) Framebuffer.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="Pipeline"/>.</summary>
    internal sealed class VeldridGpuPipeline : IGpuPipeline
    {
        internal Pipeline Pipeline { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuPipeline(DeviceLiveness liveness, Pipeline pipeline) { _liveness = liveness; Pipeline = pipeline; }
        public void Dispose() { if (_liveness.IsAlive) Pipeline.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="ResourceLayout"/>.</summary>
    internal sealed class VeldridGpuResourceLayout : IGpuResourceLayout
    {
        internal ResourceLayout Layout { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuResourceLayout(DeviceLiveness liveness, ResourceLayout layout) { _liveness = liveness; Layout = layout; }
        public void Dispose() { if (_liveness.IsAlive) Layout.Dispose(); }
    }

    /// <summary>Wraps a Veldrid <see cref="ResourceSet"/>.</summary>
    internal sealed class VeldridGpuResourceSet : IGpuResourceSet
    {
        internal ResourceSet Set { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuResourceSet(DeviceLiveness liveness, ResourceSet set) { _liveness = liveness; Set = set; }
        public void Dispose() { if (_liveness.IsAlive) Set.Dispose(); }
    }

    /// <summary>Wraps the Veldrid <see cref="Shader"/>[] a SPIR-V cross-compile produces.</summary>
    internal sealed class VeldridGpuShaderSet : IGpuShaderSet
    {
        internal Shader[] Shaders { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuShaderSet(DeviceLiveness liveness, Shader[] shaders) { _liveness = liveness; Shaders = shaders; }
        public void Dispose() { if (_liveness.IsDead) return; foreach (var s in Shaders) s.Dispose(); }
    }

    /// <summary>Wraps the single Veldrid <see cref="Shader"/> a single-stage (compute) SPIR-V cross-compile
    /// produces, carrying the workgroup size read out of the SPIR-V module (see <see cref="SpirvLocalSize"/>).</summary>
    internal sealed class VeldridGpuComputeShader : IGpuComputeShader
    {
        internal Shader Shader { get; }
        readonly DeviceLiveness _liveness;
        public uint ThreadGroupSizeX { get; }
        public uint ThreadGroupSizeY { get; }
        public uint ThreadGroupSizeZ { get; }

        public VeldridGpuComputeShader(DeviceLiveness liveness, Shader shader, uint groupX, uint groupY, uint groupZ)
        {
            _liveness = liveness; Shader = shader;
            ThreadGroupSizeX = groupX; ThreadGroupSizeY = groupY; ThreadGroupSizeZ = groupZ;
        }

        public void Dispose() { if (_liveness.IsAlive) Shader.Dispose(); }
    }

    /// <summary>Wraps a Veldrid compute <see cref="Pipeline"/> (one created from a
    /// <see cref="ComputePipelineDescription"/>).</summary>
    internal sealed class VeldridGpuComputePipeline : IGpuComputePipeline
    {
        internal Pipeline Pipeline { get; }
        readonly DeviceLiveness _liveness;
        public VeldridGpuComputePipeline(DeviceLiveness liveness, Pipeline pipeline) { _liveness = liveness; Pipeline = pipeline; }
        public void Dispose() { if (_liveness.IsAlive) Pipeline.Dispose(); }
    }
}
