using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE BARRIER A STAGED UPLOAD OWES, NARROWED TO THE DESTINATION'S ACTUAL USAGE (V-M9, section 9.3). Pure
    /// arithmetic over a flags word, so what a copy is synchronised against is a plain <c>[Fact]</c> rather than a
    /// thing only a validation layer on a real device can see.
    ///
    /// <para><b>WHAT IT REPLACES.</b> The shipped incumbent emits ONE global <c>VkMemoryBarrier</c> after every
    /// upload it makes, with <c>dstStageMask = VertexInput</c> and <c>dstAccessMask = VertexAttributeRead</c>.
    /// That is a guess, and it is wrong in both directions at once. For an index buffer, an indirect argument
    /// buffer or a storage buffer it under-synchronises: the reads that follow happen at stages the barrier never
    /// named. For a uniform buffer it is wrong in the way section 9.2 turns on, since a uniform read is
    /// <c>UniformRead</c> and not <c>VertexAttributeRead</c>. And being GLOBAL it also over-synchronises, because a
    /// barrier with no buffer range makes every access of that class wait rather than the one resource that was
    /// written.</para>
    ///
    /// <para><b>THE UNIFORM CASE IS CARRIED HERE AND IS NOT REACHED BY THE RING.</b> A ring-backed uniform buffer
    /// never stages at all: its record-time write is a memcpy into persistently mapped coherent memory with no
    /// copy, no barrier and no pass split (9.2). The uniform entry below exists for the buffer the seam can still
    /// describe and the ring policy does not cover, and for the DEVICE-level upload path, so the table is total
    /// over the enum rather than total over what happens to reach it today.</para>
    ///
    /// <para><b>THE STAGES ARE UNIONED RATHER THAN SWITCHED.</b> A buffer created vertex-and-index is one buffer
    /// read at two stages with two access types, and a switch over the flags word would have to pick one. Or-ing
    /// the contributions is both correct and the only shape that stays correct when a usage combination nobody
    /// anticipated arrives.</para>
    ///
    /// <para><b>A SHADER READ NAMES ALL THREE PROGRAMMABLE STAGES</b>, because the seam carries no stage
    /// visibility on a buffer's USAGE (that lives on the resource LAYOUT, which is rows 10 and 11's). Naming
    /// vertex, fragment and compute is the conservative answer that is still narrower than the incumbent's
    /// whole-pipeline flush, and narrowing it further would need information this call does not have.</para>
    /// </summary>
    internal static class VulkanUploadBarrier
    {
        /// <summary>The source half, which is the same for every staged upload: the transfer stage wrote the
        /// bytes.</summary>
        internal const PipelineStageFlags2 SourceStage = PipelineStageFlags2.TransferBit;

        /// <summary>The source access, likewise fixed.</summary>
        internal const AccessFlags2 SourceAccess = AccessFlags2.TransferWriteBit;

        /// <summary>
        /// The stages that read a buffer of <paramref name="usage"/>, which is the barrier's
        /// <c>dstStageMask</c>.
        /// </summary>
        internal static PipelineStageFlags2 DestinationStage(GpuBufferUsage usage)
        {
            PipelineStageFlags2 stages = PipelineStageFlags2.None;

            if ((usage & (GpuBufferUsage.VertexBuffer | GpuBufferUsage.IndexBuffer)) != 0)
            {
                stages |= PipelineStageFlags2.VertexInputBit;
            }

            if ((usage & GpuBufferUsage.IndirectBuffer) != 0)
            {
                stages |= PipelineStageFlags2.DrawIndirectBit;
            }

            if ((usage & ShaderRead) != 0)
            {
                stages |= PipelineStageFlags2.VertexShaderBit
                    | PipelineStageFlags2.FragmentShaderBit
                    | PipelineStageFlags2.ComputeShaderBit;
            }

            // A buffer with no read usage at all is still copied FROM, and a copy is a transfer read. Answering
            // None would emit a barrier that orders the write against nothing, which is the shape that passes
            // review and synchronises nothing.
            return stages == PipelineStageFlags2.None ? PipelineStageFlags2.TransferBit : stages;
        }

        /// <summary>
        /// The accesses a buffer of <paramref name="usage"/> is read through, which is the barrier's
        /// <c>dstAccessMask</c>.
        /// </summary>
        internal static AccessFlags2 DestinationAccess(GpuBufferUsage usage)
        {
            AccessFlags2 access = AccessFlags2.None;

            if ((usage & GpuBufferUsage.VertexBuffer) != 0) access |= AccessFlags2.VertexAttributeReadBit;
            if ((usage & GpuBufferUsage.IndexBuffer) != 0) access |= AccessFlags2.IndexReadBit;
            if ((usage & GpuBufferUsage.IndirectBuffer) != 0) access |= AccessFlags2.IndirectCommandReadBit;
            if ((usage & GpuBufferUsage.UniformBuffer) != 0) access |= AccessFlags2.UniformReadBit;

            if ((usage & GpuBufferUsage.StructuredBufferReadOnly) != 0)
            {
                access |= AccessFlags2.ShaderStorageReadBit;
            }

            if ((usage & GpuBufferUsage.StructuredBufferReadWrite) != 0)
            {
                access |= AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit;
            }

            return access == AccessFlags2.None ? AccessFlags2.TransferReadBit : access;
        }

        /// <summary>
        /// The whole barrier for one staged upload, as a <c>VkBufferMemoryBarrier2</c> over the WRITTEN RANGE ALONE
        /// rather than a global memory barrier. Both queue family indices are
        /// <c>VK_QUEUE_FAMILY_IGNORED</c>, because this backend has exactly one queue (V-N5) so there is no
        /// ownership transfer to express.
        /// </summary>
        /// <param name="destination">The destination <c>VkBuffer</c>.</param>
        /// <param name="offsetBytes">Where the copy wrote.</param>
        /// <param name="sizeBytes">How much it wrote.</param>
        /// <param name="usage">The destination buffer's usage, which narrows the destination masks.</param>
        internal static unsafe BufferMemoryBarrier2 For(ulong destination, ulong offsetBytes, ulong sizeBytes,
            GpuBufferUsage usage)
            => new(
                sType: StructureType.BufferMemoryBarrier2,
                srcStageMask: SourceStage,
                srcAccessMask: SourceAccess,
                dstStageMask: DestinationStage(usage),
                dstAccessMask: DestinationAccess(usage),
                srcQueueFamilyIndex: Vk.QueueFamilyIgnored,
                dstQueueFamilyIndex: Vk.QueueFamilyIgnored,
                buffer: new Buffer(destination),
                offset: offsetBytes,
                size: sizeBytes);

        const GpuBufferUsage ShaderRead =
            GpuBufferUsage.UniformBuffer
            | GpuBufferUsage.StructuredBufferReadOnly
            | GpuBufferUsage.StructuredBufferReadWrite;
    }
}
