using System;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Metal.Internal
{
    internal sealed partial class MetalGpuDevice
    {
        /// <summary>
        /// The seam's non-blocking staging read. Every buffer on this backend is a Shared <c>MTLBuffer</c> whose
        /// <c>-contents</c> pointer was taken once at creation, so a completed copy is read by polling the fence
        /// and copying from that pointer. Nothing here calls <see cref="Map(IGpuBuffer, GpuMapMode)"/>, whose read
        /// drain is the whole difference, nor <see cref="WaitForIdle"/>, the setup flush, a submit or the submit
        /// lock.
        /// <para>
        /// VISIBILITY IS THE COMPLETION'S. A Shared buffer written by a blit is visible to the CPU once the command
        /// buffer that wrote it has completed, which is what the fence's value on the shared event says. The copy
        /// was recorded into the list itself, never the setup batch, so that fence covers it.
        /// </para>
        /// </summary>
        public bool TryReadBuffer(IGpuBuffer staging, IGpuFence completion, uint offsetBytes, Span<byte> destination)
        {
            // Death first: a dead device's fence reads signalled, and its Shared memory went with it.
            ObjectDisposedException.ThrowIf(_disposed || _liveness.IsDead, this);
            ArgumentNullException.ThrowIfNull(staging);
            ArgumentNullException.ThrowIfNull(completion);

            if (completion is not MetalGpuFence fence)
            {
                throw new ArgumentException(
                    $"A {completion.GetType().Name} was handed to the native Metal backend as the completion fence "
                    + "of a staging copy. Only a fence this backend created names a value on its timeline.",
                    nameof(completion));
            }

            fence.RequireOwner(_timeline, nameof(completion));

            MetalBuffer buffer = MetalResourceOwnership.Require<MetalBuffer>(staging, _liveness, nameof(staging));
            IntPtr contents = buffer.Contents;
            ObjectDisposedException.ThrowIf(contents == IntPtr.Zero, staging);

            if ((buffer.Usage & GpuBufferUsage.Staging) == 0 || buffer.IsRingBacked)
            {
                throw new ArgumentException(
                    "This buffer was not created as a plain GpuBufferUsage.Staging buffer. Every buffer is "
                    + "CPU-visible on Metal, but the seam reads only staging buffers so the same call holds on "
                    + "Direct3D 11 and Vulkan, and a ring-backed buffer has one segment per frame in flight rather "
                    + "than one range. Record a CopyBuffer into a staging buffer and read that.", nameof(staging));
            }

            GpuReadWindow.Require(buffer.SizeInBytes, offsetBytes, destination.Length);

            if (!fence.Signaled) return false;

            GpuReadWindow.CopyOut(contents, offsetBytes, destination);
            return true;
        }
    }
}
