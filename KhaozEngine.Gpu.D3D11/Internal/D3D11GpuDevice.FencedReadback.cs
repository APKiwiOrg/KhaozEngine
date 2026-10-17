using System;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE FENCED READ MAP (<see cref="IGpuFencedReadback"/>), which on this backend is the staging map path that
    /// already exists. <c>Map(READ)</c> on the immediate context waits only for work that touches THAT resource,
    /// and never drains the device, so a fence that has signaled over the copy means the map returns without
    /// stalling. What this adds is the refusal: the fence must be this device's and must have signaled. No
    /// <c>DO_NOT_WAIT</c> retry loop, because a signaled fence leaves nothing for the map to wait on.
    /// </summary>
    internal sealed partial class D3D11GpuDevice : IGpuFencedReadback
    {
        /// <inheritdoc/>
        public MappedData MapForRead(IGpuTexture staging, IGpuFence fence)
        {
            ArgumentNullException.ThrowIfNull(staging);
            ArgumentNullException.ThrowIfNull(fence);

            if (staging is not ID3D11MappableResource { IsMappable: true })
            {
                throw new ArgumentException(
                    "MapForRead maps a GpuTextureUsage.Staging texture created by this native Direct3D 11 device, "
                    + "and this one is not, so it has no CPU access to map. Copy into a staging texture and map "
                    + "that.", nameof(staging));
            }

            if (fence is not D3D11GpuFence own || !ReferenceEquals(own.Owner, _fences))
            {
                throw new ArgumentException(
                    "MapForRead was handed a fence this native Direct3D 11 device did not create. Another device's "
                    + "fence would read signaled for work this device never ran.", nameof(fence));
            }

            if (!own.Signaled)
            {
                throw new InvalidOperationException(
                    "MapForRead was called with a fence that has not signaled, so the copy into the staging texture "
                    + "may still be running. Poll IGpuFence.Signaled first. This map never waits, by design.");
            }

            return _staging.Map(staging, GpuMapMode.Read);
        }
    }
}
