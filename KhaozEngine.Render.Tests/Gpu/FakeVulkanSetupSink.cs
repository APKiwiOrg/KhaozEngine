using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>One recorded image barrier, flattened to the fields a test asserts on.</summary>
    internal readonly record struct FakeImageBarrier(
        ulong CommandBuffer, ulong Image, ImageLayout OldLayout, ImageLayout NewLayout, ImageAspectFlags Aspect,
        uint BaseMipLevel, uint LevelCount, uint BaseArrayLayer, uint LayerCount,
        PipelineStageFlags2 SourceStage, AccessFlags2 SourceAccess,
        PipelineStageFlags2 DestinationStage, AccessFlags2 DestinationAccess);

    /// <summary>One recorded buffer barrier.</summary>
    internal readonly record struct FakeBufferBarrier(
        ulong CommandBuffer, ulong Buffer, ulong Offset, ulong Size,
        PipelineStageFlags2 DestinationStage, AccessFlags2 DestinationAccess);

    /// <summary>One recorded clear, colour or depth. <c>Aspect</c> is the CLEAR RANGE's mask, which on a combined
    /// depth-stencil format covers both planes and is why the stencil plane is not left undefined.</summary>
    internal readonly record struct FakeClear(
        ulong CommandBuffer, ulong Image, bool Depth, float Red, float DepthValue, uint LevelCount,
        uint LayerCount, ImageAspectFlags Aspect);

    /// <summary>One recorded buffer-to-image copy.</summary>
    internal readonly record struct FakeImageCopy(
        ulong CommandBuffer, ulong Source, ulong Image, ulong BufferOffset, uint BufferRowLength,
        uint BufferImageHeight, uint MipLevel, uint ArrayLayer, int X, int Y, uint Width, uint Height);

    /// <summary>One recorded buffer-to-buffer copy.</summary>
    internal readonly record struct FakeBufferCopy(
        ulong CommandBuffer, ulong Source, ulong SourceOffset, ulong Destination, ulong DestinationOffset,
        ulong Size);

    /// <summary>
    /// THE SETUP COMMAND BUFFER'S <c>vkCmd*</c> SEAM WITH NO DRIVER BEHIND IT, flattening every recorded structure
    /// into a value a test can read.
    /// <para>
    /// It is what makes decision V-M10 checkable device-free: that texture creation records ONE or TWO barriers
    /// and at most one clear, that the clear is PRESERVED and carries the incumbent's own values, that the
    /// first-ever transition comes out of <c>UNDEFINED</c> (V-F8) and lands in the canonical resting layout (V-F7),
    /// and that NOTHING is submitted per texture.
    /// </para>
    /// <para>
    /// THE STRUCTURES ARE FLATTENED AT THE SEAM rather than stored, because <c>VkDependencyInfo</c> carries raw
    /// pointer arrays as a matter of ABI. Keeping the pointer would keep a reference to a stack frame that has
    /// gone by the time the assertion reads it.
    /// </para>
    /// </summary>
    internal sealed unsafe class FakeVulkanSetupSink : IVulkanSetupSink
    {
        readonly List<string> _log = new();

        /// <summary>Every call in order, as text.</summary>
        internal IReadOnlyList<string> Events => _log;

        /// <summary>Every image barrier recorded, in order.</summary>
        internal List<FakeImageBarrier> ImageBarriers { get; } = new();

        /// <summary>Every buffer barrier recorded, in order.</summary>
        internal List<FakeBufferBarrier> BufferBarriers { get; } = new();

        /// <summary>Every clear recorded, in order.</summary>
        internal List<FakeClear> Clears { get; } = new();

        /// <summary>Every buffer-to-image copy recorded, in order.</summary>
        internal List<FakeImageCopy> ImageCopies { get; } = new();

        /// <summary>Every buffer-to-buffer copy recorded, in order.</summary>
        internal List<FakeBufferCopy> BufferCopies { get; } = new();

        /// <summary>How many commands of any kind have been recorded. The number V-M10 is about, against a submit
        /// count of zero.</summary>
        internal int CommandCount => _log.Count;

        /// <inheritdoc/>
        public void PipelineBarrier(ulong commandBuffer, in DependencyInfo dependency)
        {
            for (uint i = 0; i < dependency.ImageMemoryBarrierCount; i++)
            {
                ImageMemoryBarrier2 barrier = dependency.PImageMemoryBarriers[i];
                ImageBarriers.Add(new FakeImageBarrier(
                    commandBuffer, barrier.Image.Handle, barrier.OldLayout, barrier.NewLayout,
                    barrier.SubresourceRange.AspectMask, barrier.SubresourceRange.BaseMipLevel,
                    barrier.SubresourceRange.LevelCount, barrier.SubresourceRange.BaseArrayLayer,
                    barrier.SubresourceRange.LayerCount, barrier.SrcStageMask, barrier.SrcAccessMask,
                    barrier.DstStageMask, barrier.DstAccessMask));

                _log.Add($"vkCmdPipelineBarrier2 image {barrier.OldLayout}->{barrier.NewLayout}");
            }

            for (uint i = 0; i < dependency.BufferMemoryBarrierCount; i++)
            {
                BufferMemoryBarrier2 barrier = dependency.PBufferMemoryBarriers[i];
                BufferBarriers.Add(new FakeBufferBarrier(
                    commandBuffer, barrier.Buffer.Handle, barrier.Offset, barrier.Size, barrier.DstStageMask,
                    barrier.DstAccessMask));

                _log.Add("vkCmdPipelineBarrier2 buffer");
            }
        }

        /// <inheritdoc/>
        public void ClearColorImage(ulong commandBuffer, ulong image, in ClearColorValue color,
            in ImageSubresourceRange range)
        {
            Clears.Add(new FakeClear(commandBuffer, image, Depth: false, color.Float32_0, 0f, range.LevelCount,
                range.LayerCount, range.AspectMask));
            _log.Add("vkCmdClearColorImage");
        }

        /// <inheritdoc/>
        public void ClearDepthStencilImage(ulong commandBuffer, ulong image,
            in ClearDepthStencilValue depthStencil, in ImageSubresourceRange range)
        {
            Clears.Add(new FakeClear(commandBuffer, image, Depth: true, 0f, depthStencil.Depth, range.LevelCount,
                range.LayerCount, range.AspectMask));
            _log.Add("vkCmdClearDepthStencilImage");
        }

        /// <inheritdoc/>
        public void CopyBuffer(ulong commandBuffer, ulong source, ulong sourceOffsetBytes, ulong destination,
            ulong destinationOffsetBytes, ulong sizeBytes)
        {
            BufferCopies.Add(new FakeBufferCopy(commandBuffer, source, sourceOffsetBytes, destination,
                destinationOffsetBytes, sizeBytes));
            _log.Add("vkCmdCopyBuffer " + sizeBytes.ToString(CultureInfo.InvariantCulture));
        }

        /// <inheritdoc/>
        public void CopyBufferToImage(ulong commandBuffer, ulong source, ulong image, in BufferImageCopy region)
        {
            ImageCopies.Add(new FakeImageCopy(commandBuffer, source, image, region.BufferOffset,
                region.BufferRowLength, region.BufferImageHeight, region.ImageSubresource.MipLevel,
                region.ImageSubresource.BaseArrayLayer, region.ImageOffset.X, region.ImageOffset.Y,
                region.ImageExtent.Width, region.ImageExtent.Height));

            _log.Add("vkCmdCopyBufferToImage");
        }

        /// <summary>Reset every log, for a test that drives two phases and asserts on the second alone.</summary>
        internal void Clear()
        {
            _log.Clear();
            ImageBarriers.Clear();
            BufferBarriers.Clear();
            Clears.Clear();
            ImageCopies.Clear();
            BufferCopies.Clear();
        }
    }

    /// <summary>A liveness token a test drives directly, so the dead-device arms of the resource paths are
    /// reachable without a device to kill.</summary>
    internal sealed class FakeVulkanLiveness : IDeviceLiveness
    {
        /// <inheritdoc/>
        public bool IsDead { get; private set; }

        /// <summary>Flip it, once and for good, exactly as the real token does.</summary>
        internal void Kill() => IsDead = true;
    }
}
