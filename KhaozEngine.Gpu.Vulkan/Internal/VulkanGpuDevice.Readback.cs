using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    internal sealed partial class VulkanGpuDevice
    {
        /// <summary>
        /// The seam's non-blocking staging read. A staging buffer's host-visible allocation is persistently
        /// mapped, so a completed copy is read by polling the fence, invalidating the window on a non-coherent
        /// memory type and copying from the mapped pointer. Nothing here calls
        /// <see cref="Map(IGpuBuffer, GpuMapMode)"/>, whose timeline drain is the whole difference, nor
        /// <see cref="WaitForIdle"/>, the setup flush, a submit or the submit lock.
        /// <para>
        /// VISIBILITY IS THE RECORDED COPY'S. <c>CopyBuffer</c> ends with a barrier that makes the transfer write
        /// available to the host, the fence's value on the timeline says that submission completed, and the
        /// invalidate makes a non-coherent range visible to this read. A coherent type needs no call at all.
        /// </para>
        /// </summary>
        public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
        {
            // Death first: a dead device's fence reads signalled, and its host-visible memory went with it.
            ObjectDisposedException.ThrowIf(_disposed, this);
            RequireLiveForMap();
            ArgumentNullException.ThrowIfNull(staging);
            ArgumentNullException.ThrowIfNull(completion);

            if (completion is not VulkanGpuFence fence)
            {
                throw new ArgumentException(
                    $"A {completion.GetType().Name} was handed to the native Vulkan backend as the completion fence "
                    + "of a staging copy. Only a fence this backend created names a value on its timeline.",
                    nameof(completion));
            }

            fence.RequireOwner(_timeline, nameof(completion));

            VulkanBuffer buffer = RequireMappable(staging);
            ObjectDisposedException.ThrowIf(buffer.IsDisposed, staging);

            if (!ReferenceEquals(buffer.Owner, _resources))
            {
                throw new ArgumentException(
                    "That staging buffer was created by a DIFFERENT native Vulkan device, so its memory is not this "
                    + "device's to read. Read it through the device that created it.", nameof(staging));
            }

            GpuReadWindow.Require(buffer.SizeInBytes, offsetBytes, destination.Length);

            if (!fence.Signaled) return false;

            if (!destination.IsEmpty) buffer.Allocation.Invalidate(offsetBytes, (ulong)destination.Length);
            GpuReadWindow.CopyOut(buffer.MappedPointer, offsetBytes, destination);
            return true;
        }
    }
}
