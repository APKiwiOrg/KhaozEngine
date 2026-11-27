using System;
using System.Runtime.InteropServices;
using System.Threading;
using KhaozEngine.Gpu.Core.Interop;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe class CoreGpuDevice : IGpuDevice
{
    private khaoz_gpu_device* _handle;
    private IGpuSampler? _pointSampler;
    private IGpuSampler? _linearSampler;

    internal Lock Gate { get; } = new();
    internal bool IsDisposed => _handle == null;
    internal khaoz_gpu_device* Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            return _handle;
        }
    }

    internal CoreGpuDevice(khaoz_gpu_device* handle)
    {
        _handle = handle;
        Capabilities = ReadCapabilities(handle);
        Factory = new CoreResourceFactory(this);
    }

    public GpuBackendKind Backend => GpuBackendKind.VulkanNative;
    public GpuCapabilities Capabilities { get; }
    public IGpuResourceFactory Factory { get; }
    public IGpuFramebuffer? SwapchainFramebuffer => null;

    public IGpuSampler PointSampler
    {
        get
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return _pointSampler ??= CreateSharedSampler(GpuSamplerFilter.MinPointMagPointMipPoint);
            }
        }
    }

    public IGpuSampler LinearSampler
    {
        get
        {
            lock (Gate)
            {
                ObjectDisposedException.ThrowIf(IsDisposed, this);
                return _linearSampler ??= CreateSharedSampler(GpuSamplerFilter.MinLinearMagLinearMipLinear);
            }
        }
    }

    private IGpuSampler CreateSharedSampler(GpuSamplerFilter filter)
    {
        const GpuSamplerAddress wrap = GpuSamplerAddress.Wrap;
        return Factory.CreateSampler(new GpuSamplerDescription(filter, wrap, wrap, wrap));
    }

    internal uint ValidationErrors
    {
        get
        {
            lock (Gate)
                return IsDisposed ? DisposeValidationErrors : KhaozNative.khaoz_gpu_device_validation_errors(Handle);
        }
    }

    internal uint DisposeValidationErrors { get; private set; }
    internal static uint LastDisposedValidationErrors { get; private set; }

    public void Submit(IGpuCommandList cl) => SubmitCore(cl, null);

    public void Submit(IGpuCommandList cl, IGpuFence fence)
    {
        ArgumentNullException.ThrowIfNull(fence);
        SubmitCore(cl, fence);
    }

    private void SubmitCore(IGpuCommandList cl, IGpuFence? fence)
    {
        lock (Gate)
        {
            var commands = CoreResource.Require<CoreCommandList>(this, cl, nameof(cl), "command list");
            var completion = fence is null ? null : CoreResource.Require<CoreFence>(this, fence, nameof(fence), "fence");
            commands.Submit(completion);
        }
    }

    public void WaitForIdle()
    {
        lock (Gate)
        {
            if (!IsDisposed)
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_device_wait_for_idle(Handle), nameof(WaitForIdle));
        }
    }

    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, T[] data) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(data);
        UpdateBuffer<T>(b, offsetBytes, data.AsSpan());
    }

    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
        => UpdateBuffer(b, offsetBytes, MemoryMarshal.CreateReadOnlySpan(in data, 1));

    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        var target = CoreResource.Require<CoreBuffer>(this, b, nameof(b), "buffer");
        var bytes = MemoryMarshal.AsBytes(data);
        lock (Gate)
        fixed (byte* source = bytes)
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_buffer_update(
                target.Handle, offsetBytes, source, (uint)bytes.Length), nameof(UpdateBuffer));
    }

    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
        => UpdateTexture(texture, data, x, y, width, height, 0, 0);

    public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
        uint mipLevel, uint arrayLayer)
    {
        ArgumentNullException.ThrowIfNull(data);
        var target = CoreResource.Require<CoreTexture>(this, texture, nameof(texture), "texture");
        var region = new khaoz_gpu_texture_region
        {
            x = x, y = y, width = width, height = height, mip_level = mipLevel, array_layer = arrayLayer,
        };
        lock (Gate)
        fixed (byte* source = data)
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_texture_update(
                target.Handle, &region, source, (uint)data.Length), nameof(UpdateTexture));
    }

    public MappedData Map(IGpuTexture staging, GpuMapMode mode)
    {
        lock (Gate)
            return RequireStaging(staging).Map(mode);
    }

    public void Unmap(IGpuTexture staging)
    {
        lock (Gate)
            RequireStaging(staging).Unmap();
    }

    public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
    {
        lock (Gate)
            return RequireStaging(staging).Map(mode);
    }

    public void Unmap(IGpuBuffer staging)
    {
        lock (Gate)
            RequireStaging(staging).Unmap();
    }

    private CoreTexture RequireStaging(IGpuTexture staging)
    {
        var target = CoreResource.Require<CoreTexture>(this, staging, nameof(staging), "texture");
        if (target.IsStaging)
            return target;

        throw new GpuStagingMapArgumentException(GpuStagingMapRefusal.StagingUsageRequired,
            "Only staging textures can be mapped.", nameof(staging))
        {
            Backend = Backend,
            ResourceKind = "texture",
            RequiredTextureUsage = GpuTextureUsage.Staging,
        };
    }

    private CoreBuffer RequireStaging(IGpuBuffer staging)
    {
        var target = CoreResource.Require<CoreBuffer>(this, staging, nameof(staging), "buffer");
        if (target.IsStaging)
            return target;

        throw new GpuStagingMapArgumentException(GpuStagingMapRefusal.StagingUsageRequired,
            "Only staging buffers can be mapped.", nameof(staging))
        {
            Backend = Backend,
            ResourceKind = "buffer",
            RequiredBufferUsage = GpuBufferUsage.Staging,
        };
    }

    public void ResizeSwapchain(uint w, uint h) => throw new NotSupportedException();
    public void Present() => throw new NotSupportedException();
    public bool SyncToVerticalBlank { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public void Dispose()
    {
        lock (Gate)
        {
            if (IsDisposed)
                return;

            try
            {
                _pointSampler?.Dispose();
                _linearSampler?.Dispose();
            }
            finally
            {
                uint errors;
                var handle = _handle;
                _handle = null;
                var result = KhaozNative.khaoz_gpu_device_destroy(handle, &errors);
                DisposeValidationErrors = errors;
                LastDisposedValidationErrors = errors;
                NativeResult.ThrowIfFailed(result, nameof(Dispose));
            }
        }
    }

    private static GpuCapabilities ReadCapabilities(khaoz_gpu_device* device)
    {
        khaoz_gpu_capabilities caps;
        NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_device_capabilities(device, &caps), nameof(ReadCapabilities));
        return GpuCapabilities.FromSupportedMsaaSampleCounts(
            clipSpaceYInverted: caps.clip_space_y_inverted != 0,
            depthRangeZeroToOne: caps.depth_range_zero_to_one != 0,
            supportedMsaaSampleCounts: (GpuSampleCounts)caps.supported_msaa_sample_counts,
            deviceName: Marshal.PtrToStringUTF8((nint)(&caps.device_name.e0)) ?? string.Empty,
            samplerAnisotropy: caps.sampler_anisotropy != 0,
            samplerLodBias: caps.sampler_lod_bias != 0,
            supportsShadowMaps: caps.supports_shadow_maps != 0,
            supportsCompute: caps.supports_compute != 0,
            supportsCompletionFences: caps.supports_completion_fences != 0) with
        {
            MaxColorAttachments = (int)caps.max_color_attachments,
            MaxFragmentStorageBuffers = (int)caps.max_fragment_storage_buffers,
            MaxRenderTexture2DDimension = caps.max_render_texture_2d_dimension,
            MaxStorageBufferRangeBytes = caps.max_storage_buffer_range_bytes,
            SupportsShaderFloatControls = caps.supports_shader_float_controls != 0,
            SupportsTimestamps = caps.supports_timestamps != 0,
        };
    }
}
