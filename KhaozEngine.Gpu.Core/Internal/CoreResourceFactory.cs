using System;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreResourceFactory(CoreGpuDevice device) : IGpuResourceFactory
{
    public IGpuBuffer CreateBuffer(in GpuBufferDescription d)
    {
        lock (device.Gate)
        {
            khaoz_gpu_buffer* buffer;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_buffer_create(
                device.Handle, d.SizeInBytes, (uint)d.Usage, &buffer), nameof(CreateBuffer));
            return new CoreBuffer(device, buffer, d);
        }
    }

    public IGpuTexture CreateTexture(in GpuTextureDescription d)
    {
        var view = VulkanViewPolicy.ForTexture(d.Usage);
        var desc = new khaoz_gpu_texture_desc
        {
            width = d.Width,
            height = d.Height,
            mip_levels = d.MipLevels,
            array_layers = d.ArrayLayers,
            format = (uint)d.Format,
            usage = (uint)d.Usage,
            sample_count = d.SampleCount,
            is_array = d.IsArray ? 1u : 0u,
        };
        lock (device.Gate)
        {
            khaoz_gpu_texture* texture;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_texture_create(
                device.Handle, &desc, &texture), nameof(CreateTexture));
            return new CoreTexture(device, texture, d, view.ActualArrayLayers(d.ArrayLayers));
        }
    }

    public IGpuSampler CreateSampler(in GpuSamplerDescription d)
    {
        var desc = new khaoz_gpu_sampler_desc
        {
            filter = (uint)d.Filter,
            address_u = (uint)d.AddressModeU,
            address_v = (uint)d.AddressModeV,
            address_w = (uint)d.AddressModeW,
            maximum_anisotropy = d.MaximumAnisotropy,
            mip_lod_bias = d.MipLodBias,
        };
        lock (device.Gate)
        {
            khaoz_gpu_sampler* sampler;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_sampler_create(
                device.Handle, &desc, &sampler), nameof(CreateSampler));
            return new CoreSampler(device, sampler);
        }
    }

    public IGpuFence CreateFence()
    {
        lock (device.Gate)
        {
            khaoz_gpu_fence* fence;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_fence_create(device.Handle, &fence), nameof(CreateFence));
            return new CoreFence(device, fence);
        }
    }

    public IGpuCommandList CreateCommandList()
    {
        lock (device.Gate)
        {
            khaoz_gpu_command_list* commands;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_create(
                device.Handle, &commands), nameof(CreateCommandList));
            return new CoreCommandList(device, commands);
        }
    }

    public IGpuFramebuffer CreateFramebuffer(IGpuTexture? depth, params IGpuTexture[] colour)
        => CoreFramebuffer.Create(device, depth, colour);

    public IGpuResourceLayout CreateResourceLayout(in GpuResourceLayoutDescription d)
        => CoreResourceLayout.Create(device, d);

    public IGpuResourceSet CreateResourceSet(in GpuResourceSetDescription d)
        => CoreResourceSet.Create(device, d);

    public IGpuShaderSet CreateShadersFromSpirv(string vertGlsl, string fragGlsl)
        => CoreShaderSet.Create(device, vertGlsl, fragGlsl);

    public IGpuComputeShader CreateComputeShaderFromSpirv(string computeGlsl) => throw new NotSupportedException();
    public IGpuPipeline CreateGraphicsPipeline(in GpuPipelineDescription d)
        => CorePipeline.Create(device, d);

    public IGpuComputePipeline CreateComputePipeline(in GpuComputePipelineDescription d) => throw new NotSupportedException();
}
