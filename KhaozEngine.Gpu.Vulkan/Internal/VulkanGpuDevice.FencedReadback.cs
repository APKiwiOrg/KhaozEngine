using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE FENCED READ MAP (<see cref="IGpuFencedReadback"/>): <c>Map(staging, Read)</c> without V-C8's drain.
    /// <see cref="Map(IGpuTexture, GpuMapMode)"/> flushes the setup buffer and then waits on the timeline's last
    /// submitted value, which covers every submission made since the copy. A caller holding a signaled fence over
    /// the copy already has the completion proof, because a fence is a value on the same timeline semaphore.
    /// <para>
    /// <b>HOST VISIBILITY IS TWO STEPS, AND NEITHER IS THE WAIT.</b> A timeline signal's memory dependency covers
    /// device access only, so the copy path ends every copy into a staging texture with a transfer-to-host barrier
    /// (<see cref="VulkanTransferBarrier.ToHost"/>), which makes the copy's writes available to the host domain
    /// inside the fenced submission. Then the map invalidates the texture's range, which is real work on the
    /// cached non-coherent type the readback ladder can land on and free on a coherent one. The second step is
    /// <see cref="OpenTextureMap"/>, shared with <c>Map</c>.
    /// </para>
    /// <para>
    /// <b>NO SETUP FLUSH.</b> <c>Map</c> flushes so a read sees a render target's creation-time clear. The fenced
    /// submission already flushed the batch ahead of itself, and a flush is a <c>vkQueueSubmit</c> the read does
    /// not need.
    /// </para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice : IGpuFencedReadback
    {
        /// <inheritdoc/>
        public MappedData MapForRead(IGpuTexture staging, IGpuFence fence)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(fence);
            RequireLiveForMap();

            VulkanTexture target = RequireMappable(staging);

            if (fence is not VulkanGpuFence own || !ReferenceEquals(own.Timeline, _timeline))
            {
                throw new ArgumentException(
                    "MapForRead was handed a fence this native Vulkan device did not create. A fence is a value on "
                    + "its own device's timeline, so another device's fence would read signaled for work this "
                    + "device never ran.", nameof(fence));
            }

            if (!own.Signaled)
            {
                throw new InvalidOperationException(
                    "MapForRead was called with a fence that has not signaled, so the copy into the staging texture "
                    + "may still be running. Poll IGpuFence.Signaled first. This map never waits, by design.");
            }

            return OpenTextureMap(target, GpuMapMode.Read);
        }
    }
}
