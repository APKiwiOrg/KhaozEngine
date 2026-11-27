using System;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Gpu.Vulkan.Internal;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe partial class CoreCommandList(CoreGpuDevice owner, khaoz_gpu_command_list* handle)
    : CoreResource(owner, handle), IGpuCommandList
{
    private bool _recording;
    private bool _sealed;
    private khaoz_gpu_command_list* Handle => (khaoz_gpu_command_list*)NativeHandle;

    public void Begin()
    {
        lock (Owner.Gate)
        {
            var commands = Handle;
            if (_recording)
                throw RecordingError(GpuRecordingStateRefusal.BeginWhileRecording);

            _sealed = false;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_begin(commands), nameof(Begin));
            ResetGraphicsBindings();
            _recording = true;
        }
    }

    public void End()
    {
        lock (Owner.Gate)
        {
            var commands = Handle;
            if (!_recording)
                throw RecordingError(_sealed ? GpuRecordingStateRefusal.RecordingAlreadySealed
                    : GpuRecordingStateRefusal.EndWithoutRecording);

            _recording = false;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_end(commands), nameof(End));
            _sealed = true;
        }
    }

    internal void Submit(CoreFence? completion)
    {
        var commands = Handle;
        if (!_sealed)
            throw RecordingError(GpuRecordingStateRefusal.SubmissionWithoutSeal);

        NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_device_submit(Owner.Handle, commands,
            completion is null ? null : completion.Handle), nameof(Submit));
        _sealed = false;
    }

    public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes)
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording();
            var source = Require<CoreBuffer>(Owner, src, nameof(src), "buffer");
            var destination = Require<CoreBuffer>(Owner, dst, nameof(dst), "buffer");
            RequireAligned(srcOffsetBytes, nameof(srcOffsetBytes), "source");
            RequireAligned(dstOffsetBytes, nameof(dstOffsetBytes), "destination");
            RequireBufferWindow(source, srcOffsetBytes, sizeInBytes, "source");
            RequireBufferWindow(destination, dstOffsetBytes, sizeInBytes, "destination");
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_copy_buffer(
                commands, source.Handle, srcOffsetBytes, destination.Handle, dstOffsetBytes, sizeInBytes), nameof(CopyBuffer));
        }
    }

    public void CopyTexture(IGpuTexture src, IGpuTexture dst)
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording();
            var source = Require<CoreTexture>(Owner, src, nameof(src), "texture");
            var destination = Require<CoreTexture>(Owner, dst, nameof(dst), "texture");
            RequireMatchingShape(source, destination);
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_copy_texture(
                commands, source.Handle, destination.Handle, null), nameof(CopyTexture));
        }
    }

    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
        uint width, uint height)
        => CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, 0, 0, width, height);

    public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
        uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
    {
        var copy = new khaoz_gpu_texture_copy
        {
            src_mip_level = srcMipLevel,
            src_array_layer = srcArrayLayer,
            dst_mip_level = dstMipLevel,
            dst_array_layer = dstArrayLayer,
            width = width,
            height = height,
        };
        lock (Owner.Gate)
        {
            var commands = RequireRecording();
            var source = Require<CoreTexture>(Owner, src, nameof(src), "texture");
            var destination = Require<CoreTexture>(Owner, dst, nameof(dst), "texture");
            var sourceHandle = source.Handle;
            var destinationHandle = destination.Handle;
            if (!source.HasCompatibleCopyFormat(destination))
                throw IncompatibleCopy(source, destination);
            VulkanUploadBounds.RequireMipLevel(srcMipLevel, source.MipLevels, nameof(srcMipLevel), "source");
            VulkanUploadBounds.RequireArrayLayer(srcArrayLayer, source.ArrayLayers, nameof(srcArrayLayer), "source");
            VulkanUploadBounds.RequireMipLevel(dstMipLevel, destination.MipLevels, nameof(dstMipLevel), "destination");
            VulkanUploadBounds.RequireArrayLayer(dstArrayLayer, destination.ArrayLayers, nameof(dstArrayLayer), "destination");
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_copy_texture(
                commands, sourceHandle, destinationHandle, &copy), nameof(CopyTextureSubresource));
        }
    }

    private khaoz_gpu_command_list* RequireRecording([CallerMemberName] string operation = "")
    {
        var commands = Handle;
        if (_recording)
            return commands;

        throw RecordingError(GpuRecordingStateRefusal.CommandOutsideRecording, operation);
    }

    private static GpuRecordingStateException RecordingError(GpuRecordingStateRefusal reason,
        [CallerMemberName] string operation = "")
        => new(reason, $"The command list is in the wrong recording state for {operation}.")
        {
            Backend = GpuBackendKind.VulkanNative,
            Command = operation,
        };

    private static void RequireAligned(uint offset, string parameter, string side)
    {
        const uint alignment = 4;
        if (offset % alignment == 0)
            return;

        throw new GpuCopyAlignmentException(GpuCopyAlignmentRefusal.UnalignedOffset, parameter, (ulong)offset,
            $"The {side} buffer offset must be aligned to {alignment} bytes.")
        {
            Context = nameof(CopyBuffer),
            Side = side,
            AlignmentBytes = alignment,
            RecoveryOperation = "IGpuDevice.UpdateBuffer",
        };
    }

    private static void RequireBufferWindow(CoreBuffer buffer, uint offset, uint size, string side)
    {
        const string sizeParameter = "sizeInBytes";
        if (size == 0)
            throw new VulkanBufferCopyException(VulkanBufferCopyRefusal.EmptyCopy, sizeParameter, size,
                "A Vulkan buffer copy requires a nonzero size.") { SizeBytes = size, Side = side };

        if (offset <= buffer.SizeInBytes && size <= buffer.SizeInBytes - offset)
            return;

        throw new GpuBufferTransferRangeException(GpuBufferTransferRefusal.WindowOutsideBuffer, sizeParameter, size,
            $"The copy window exceeds the {side} buffer.")
        {
            Side = side,
            OffsetBytes = offset,
            SizeBytes = size,
            BufferBytes = buffer.SizeInBytes,
        };
    }

    private static void RequireMatchingShape(CoreTexture source, CoreTexture destination)
    {
        if (source.Width == destination.Width && source.Height == destination.Height
            && source.MipLevels == destination.MipLevels && source.ArrayLayers == destination.ArrayLayers
            && source.HasCompatibleCopyFormat(destination))
            return;

        throw IncompatibleCopy(source, destination);
    }

    private static GpuTextureTransferArgumentException IncompatibleCopy(CoreTexture source, CoreTexture destination)
        => new(GpuTextureTransferRefusal.TextureShapeMismatch,
            "Texture copies require matching native formats and sample counts. Whole copies also require matching extents, mips and layers.",
            nameof(destination))
        {
            SourceWidth = source.Width,
            SourceHeight = source.Height,
            SourceFormat = source.Format,
            SourceMipLevels = source.MipLevels,
            SourceArrayLayers = source.ArrayLayers,
            SourceSampleCount = source.SampleCount,
            DestinationWidth = destination.Width,
            DestinationHeight = destination.Height,
            DestinationFormat = destination.Format,
            DestinationMipLevels = destination.MipLevels,
            DestinationArrayLayers = destination.ArrayLayers,
            DestinationSampleCount = destination.SampleCount,
        };

    public void GenerateMipmaps(IGpuTexture texture) => throw new NotSupportedException();
    public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => throw new NotSupportedException();
    public void SetComputePipeline(IGpuComputePipeline p) => throw new NotSupportedException();
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => throw new NotSupportedException();
    public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) => throw new NotSupportedException();
    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => throw new NotSupportedException();

    protected override khaoz_result Release(void* value)
        => KhaozNative.khaoz_gpu_command_list_destroy((khaoz_gpu_command_list*)value);
}
