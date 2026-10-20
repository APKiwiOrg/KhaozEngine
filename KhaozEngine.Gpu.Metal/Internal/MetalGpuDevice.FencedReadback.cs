using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE FENCED READ MAP (<see cref="IGpuFencedReadback"/>): <c>Map(staging, Read)</c> without the device-wide
    /// drain. <see cref="Map(IGpuTexture, GpuMapMode)"/> routes a read through <c>DrainForRead</c>, which flushes
    /// the setup batch and then calls <see cref="WaitForIdle"/>, a wait for everything ever submitted. A caller
    /// holding a signaled fence over the copy that wrote the staging texture already has the only completion
    /// proof the read needs.
    /// <para>
    /// <b>NO STORAGE-MODE SYNCHRONISATION IS NEEDED, BECAUSE THERE IS NO MANAGED STORAGE.</b> Every staging texture
    /// on this backend is an <c>MTLStorageModeShared</c> <c>MTLBuffer</c> (<see cref="MetalTexture"/>, M-M2), on
    /// Apple silicon and Intel Macs alike, so there is no <c>synchronizeResource:</c> a managed resource would
    /// need encoded into the producing submission. Shared memory has one copy that the CPU and GPU both address.
    /// The fence is a value on the device's one <c>MTLSharedEvent</c>, which the submit path encodes after the
    /// command buffer's work, so a completed value means the blit into the buffer has completed.
    /// </para>
    /// <para>
    /// <b>NO SETUP FLUSH EITHER.</b> The flush in <c>DrainForRead</c> exists so a read sees uploads made through
    /// the setup batch. The batch only ever carries uploads into Private textures, a staging texture is written by
    /// a <c>memcpy</c> or by a copy in a command list, and every <c>Submit</c> flushes the batch ahead of its own
    /// commit, so the fenced submission already ran behind anything the batch held when it was made.
    /// </para>
    /// </summary>
    internal sealed partial class MetalGpuDevice : IGpuFencedReadback
    {
        /// <inheritdoc/>
        public MappedData MapForRead(IGpuTexture staging, IGpuFence fence)
        {
            ArgumentNullException.ThrowIfNull(staging);
            ArgumentNullException.ThrowIfNull(fence);

            MetalTexture metal = MetalResourceOwnership.Require<MetalTexture>(staging, _liveness, nameof(staging));
            if (!metal.IsStaging)
            {
                throw new ArgumentException(
                    "MapForRead maps a GpuTextureUsage.Staging texture, and this one is a Private MTLTexture with no "
                    + "CPU-visible memory. Copy into a staging texture and map that.", nameof(staging));
            }

            MetalGpuFence own = RequireFence(fence, _timeline);
            if (!own.Signaled)
            {
                throw new InvalidOperationException(
                    "MapForRead was called with a fence that has not signaled, so the copy into the staging texture "
                    + "may still be running. Poll IGpuFence.Signaled first. This map never waits, by design.");
            }

            return metal.Mapped(subresource: 0);
        }
    }
}
