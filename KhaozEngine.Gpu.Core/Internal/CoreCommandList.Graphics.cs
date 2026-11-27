using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Core.Interop;
using KhaozEngine.Gpu.Vulkan;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Core.Internal;

internal sealed unsafe partial class CoreCommandList
{
    private CoreFramebuffer? _framebuffer;
    private CorePipeline? _pipeline;
    private readonly Dictionary<uint, (CoreResourceSet Set, uint Offset)> _sets = [];

    private void ResetGraphicsBindings()
    {
        _framebuffer = null;
        _pipeline = null;
        _sets.Clear();
    }

    public void SetFramebuffer(IGpuFramebuffer fb)
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording("Binding a framebuffer");
            var target = RequireOwned<CoreFramebuffer>(Owner, fb, "framebuffer", "framebuffer",
                "a native Vulkan framebuffer bind");
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_set_framebuffer(commands, target.Handle),
                nameof(SetFramebuffer));
            _framebuffer = target;
        }
    }

    public void ClearColorTarget(uint index, Color rgba)
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording("Clearing a colour target");
            var target = RequireFramebuffer(nameof(ClearColorTarget));
            if (index >= (uint)target.Outputs.Colour.Length)
                throw new ArgumentOutOfRangeException(nameof(index), index, "The colour attachment is not bound.");

            var clear = new khaoz_gpu_color { r = rgba.R, g = rgba.G, b = rgba.B, a = rgba.A };
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_clear_color(
                commands, index, &clear), nameof(ClearColorTarget));
        }
    }

    public void ClearDepthStencil(float depth)
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording("Clearing the depth attachment");
            var target = RequireFramebuffer(nameof(ClearDepthStencil));
            if (!target.Outputs.Depth.HasValue)
                throw new GpuBoundFramebufferException(GpuBoundFramebufferRefusal.DepthAttachmentMissing,
                    "The bound framebuffer has no depth attachment.") { Command = nameof(ClearDepthStencil) };

            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_clear_depth(commands, depth),
                nameof(ClearDepthStencil));
        }
    }

    public void SetPipeline(IGpuPipeline p)
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording("Binding a graphics pipeline");
            ArgumentNullException.ThrowIfNull(p);
            var pipeline = Require<CorePipeline>(Owner, p, "pipeline", "graphics pipeline", "a native Vulkan graphics pipeline bind");
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_set_pipeline(commands, pipeline.Handle), nameof(SetPipeline));
            _pipeline = pipeline;
        }
    }

    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => SetGraphicsResourceSet(slot, set, 0);

    public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
    {
        lock (Owner.Gate)
        {
            var commands = Handle;
            var binding = set is null ? null : Require<CoreResourceSet>(Owner, set, nameof(set), "resource set", "a native Vulkan bind");
            if (binding is { HasDynamicElement: false } && dynamicOffset != 0)
                throw new GpuDynamicBindingException(GpuDynamicBindingRefusal.DynamicElementMissing,
                    "The resource set has no declared dynamic binding.", nameof(dynamicOffset))
                {
                    Backend = GpuBackendKind.VulkanNative,
                    Set = slot,
                    DynamicOffset = dynamicOffset,
                };

            // Like the managed Vulkan binder, an out-of-recording bind is discarded by Begin.
            if (!_recording)
                return;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_set_resource_set(
                commands, slot, binding is null ? null : binding.Handle, dynamicOffset), nameof(SetGraphicsResourceSet));
            if (binding is null)
                _sets.Remove(slot);
            else
                _sets[slot] = (binding, dynamicOffset);
        }
    }

    public void SetVertexBuffer(uint slot, IGpuBuffer b) => SetVertexBuffer(slot, b, 0);

    public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
    {
        lock (Owner.Gate)
        {
            var buffer = RequireBufferUsage(b, GpuBufferUsage.VertexBuffer, "a native Vulkan vertex buffer bind");
            var commands = Handle;
            if (!_recording)
                return;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_set_vertex_buffer(
                commands, slot, buffer.Handle, offsetBytes), nameof(SetVertexBuffer));
        }
    }

    public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
    {
        lock (Owner.Gate)
        {
            var buffer = RequireBufferUsage(b, GpuBufferUsage.IndexBuffer, "a native Vulkan index buffer bind");
            var commands = Handle;
            if (!_recording)
                return;
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_set_index_buffer(
                commands, buffer.Handle, (uint)fmt), nameof(SetIndexBuffer));
        }
    }

    public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording("Setting a scissor rect");
            if (index != 0)
                throw new ArgumentOutOfRangeException(nameof(index), index, "Vulkan exposes one scissor at index zero.");

            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_set_scissor(
                commands, checked((int)x), checked((int)y), w, h), nameof(SetScissorRect));
        }
    }

    public void SetFullScissorRects()
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording("Resetting the scissor rects");
            var target = RequireFramebuffer(nameof(SetFullScissorRects));
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_set_scissor(
                commands, 0, 0, target.Width, target.Height), nameof(SetFullScissorRects));
        }
    }

    public void Draw(uint vertexCount) => Draw(vertexCount, 1, 0, 0);

    public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
    {
        lock (Owner.Gate)
        {
            var commands = RequireDrawState(nameof(Draw));
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_draw(
                commands, vertexCount, instanceCount, vertexStart, instanceStart), nameof(Draw));
        }
    }

    public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
    {
        lock (Owner.Gate)
        {
            var commands = RequireDrawState(nameof(DrawIndexed));
            NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_draw_indexed(
                commands, indexCount, instanceCount, indexStart, vertexOffset, instanceStart), nameof(DrawIndexed));
        }
    }

    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
        => UpdateBuffer(b, offsetBytes, MemoryMarshal.CreateReadOnlySpan(in data, 1));

    public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
    {
        lock (Owner.Gate)
        {
            var commands = RequireRecording();
            var target = Require<CoreBuffer>(Owner, b, "buffer", "buffer");
            var bytes = MemoryMarshal.AsBytes(data);
            fixed (byte* source = bytes)
                NativeResult.ThrowIfFailed(KhaozNative.khaoz_gpu_command_list_update_buffer(
                    commands, target.Handle, offsetBytes, source, (uint)bytes.Length), nameof(UpdateBuffer));
        }
    }

    private CoreFramebuffer RequireFramebuffer(string command)
        => _framebuffer ?? throw new GpuBoundFramebufferException(GpuBoundFramebufferRefusal.FramebufferNotBound,
            "Bind a framebuffer before recording this Vulkan command.") { Command = command };

    private khaoz_gpu_command_list* RequireDrawState(string operation)
    {
        var commands = RequireRecording(operation);
        if (_pipeline is null)
            throw RecordingError(GpuRecordingStateRefusal.GraphicsPipelineNotBound, operation);
        _ = _pipeline.Handle;
        _ = RequireFramebuffer(operation).Handle;
        foreach (var (slot, (set, offset)) in _sets)
        {
            if (_pipeline.Accepts(slot, set))
            {
                _ = set.Handle;
                set.ValidateOffsets(slot, offset);
            }
        }
        return commands;
    }

    private CoreBuffer RequireBufferUsage(IGpuBuffer b, GpuBufferUsage required, string context)
    {
        var buffer = RequireOwned<CoreBuffer>(Owner, b, "buffer", "buffer", context);
        if ((buffer.Usage & required) == 0)
            throw new VulkanBufferBindingException(VulkanBufferBindingRefusal.RequiredUsageMissing,
                "The bound buffer lacks the required Vulkan usage.", nameof(buffer))
            {
                Usage = buffer.Usage,
                RequiredUsage = required,
                Context = context,
            };
        return buffer;
    }
}
