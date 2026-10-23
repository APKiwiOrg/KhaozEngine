using System;
using KhaozEngine.Gpu.Vulkan.Internal;
using KhaozEngine.Primitives;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu.CoveragePerf;

/// <summary>Faults at the existing rendering and layout seams, with command-buffer lifecycle validation.</summary>
internal sealed class TimestampEndRenderApi(FakeVulkanRenderApi render, FakeVulkanBarrierRecorder barriers,
    TimestampEndCommandApi commands) : IVulkanRenderApi, IVulkanBarrierRecorder
{
    internal bool FailBegin;
    internal bool FailEnd;
    internal bool FailBarrier;

    public void BeginRendering(ulong commandBuffer, uint width, uint height,
        ReadOnlySpan<VulkanColourAttachment> colour, VulkanDepthAttachment? depth)
    {
        commands.RequireRecording(commandBuffer);
        if (FailBegin) commands.ThrowFailure(commandBuffer);
        render.BeginRendering(commandBuffer, width, height, colour, depth);
    }

    public void EndRendering(ulong commandBuffer)
    {
        commands.RequireRecording(commandBuffer);
        if (FailEnd) commands.ThrowFailure(commandBuffer);
        render.EndRendering(commandBuffer);
    }

    public void Emit(ulong commandBuffer, ReadOnlySpan<ImageMemoryBarrier2> batch)
    {
        commands.RequireRecording(commandBuffer);
        if (FailBarrier) commands.ThrowFailure(commandBuffer);
        barriers.Emit(commandBuffer, batch);
    }

    public void SetViewport(ulong commandBuffer, in VulkanViewportRect viewport)
    {
        commands.RequireRecording(commandBuffer);
        render.SetViewport(commandBuffer, viewport);
    }

    public void SetScissor(ulong commandBuffer, in VulkanScissorRect scissor)
    {
        commands.RequireRecording(commandBuffer);
        render.SetScissor(commandBuffer, scissor);
    }

    public void ClearColourAttachment(ulong commandBuffer, uint index, Color rgba, uint width, uint height)
    {
        commands.RequireRecording(commandBuffer);
        render.ClearColourAttachment(commandBuffer, index, rgba, width, height);
    }

    public void ClearDepthAttachment(ulong commandBuffer, float depth, bool stencil, uint width, uint height)
    {
        commands.RequireRecording(commandBuffer);
        render.ClearDepthAttachment(commandBuffer, depth, stencil, width, height);
    }
}

internal sealed class TimestampEndUploads : IVulkanRecordUploads
{
    internal int Uploads;
    public void BeginSlot(int slot) { }
    public void Upload(IVulkanUploadDestination destination, ulong destinationOffsetBytes, ReadOnlySpan<byte> data)
        => Uploads++;
}
