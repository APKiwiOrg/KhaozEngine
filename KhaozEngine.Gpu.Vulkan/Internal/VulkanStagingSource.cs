using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE TWO REAL DRIVER CALLS BEHIND <see cref="IVulkanStagingSource"/>: a host-visible, coherent, persistently
    /// mapped <c>VkBuffer</c> out of the device's block allocator, and its deferred destroy. Section 9.3.
    ///
    /// <para><b>THE DEFERRAL IS THE CONTRACT AND IT LIVES HERE RATHER THAN IN THE ARENA.</b>
    /// <see cref="VulkanStagingArena.Dispose"/> destroys every block it holds UNGATED, because an arena has no
    /// timeline and no way to know what is in flight. So the gate is this type's:
    /// <see cref="Destroy"/> hands the native free to the device's retire list rather than making it, and an
    /// in-flight submission that is still reading a block finishes first. Freeing immediately is exactly the
    /// corruption class the arena's own slot gate exists to prevent, arriving through the one call the arena trusts
    /// to be safe.</para>
    ///
    /// <para><b>THE GATE VALUE IS THE TIMELINE'S LAST ALLOCATED ONE, WHICH IS AT OR ABOVE THE HIGHEST SUBMITTED
    /// ONE.</b> The contract asks for the highest submitted value, the way the command pools retire behind theirs.
    /// The allocated value satisfies that and closes one window the submitted value leaves open: a submission
    /// sitting between taking its value and registering it has not raised the submitted high-water yet, and a
    /// destroy gated on the lower number would run underneath it. See
    /// <see cref="VulkanResourceOwner.RetireTerminal"/>, which every resource on this backend goes through.</para>
    ///
    /// <para><b>A DEAD DEVICE ABANDONS RATHER THAN FREES.</b> The block's buffer and its memory went with the
    /// device, so a <c>vkDestroyBuffer</c> or a <c>vkFreeMemory</c> now is a call against memory the driver already
    /// released, which aborts the process through the Vulkan loader rather than failing quietly. That matches
    /// <see cref="VulkanRetireList.Abandon"/> and <see cref="VulkanMemoryAllocator.Abandon"/>, and it is reached by
    /// the same liveness token.</para>
    ///
    /// <para><b>THE DESTROY IS TERMINAL.</b> One retired entry destroys the buffer INLINE and then frees its
    /// suballocation, which may retire the CHUNK the suballocation came out of. That is one further generation and
    /// exactly the one the device's teardown drains twice for, so the depth is bounded at two by construction. No
    /// entry here retires another entry.</para>
    ///
    /// <para><b>IT IS THREAD-SAFE, UNLIKE THE ARENAS ABOVE IT.</b> One source serves EVERY arena on the device: the
    /// device-owned one behind the setup buffer and one per command list. Each arena is single-threaded by
    /// construction and they are different threads from each other, so the allocation ledger below takes a short
    /// lock. Without it two lists creating a block at once would race a dictionary and lose an allocation, which
    /// presents as a leak nobody can attribute. The two DESTROY COUNTERS are incremented after that lock is
    /// released, so they are atomic rather than guarded (https://github.com/APKiwiOrg/KhaozEngine/issues/551): a
    /// plain increment there could drop a destroy and present as the same unattributable leak, through the very
    /// reading that exists to rule one out.</para>
    /// </summary>
    internal sealed class VulkanStagingSource : IVulkanStagingSource
    {
        readonly VulkanResourceOwner _owner;
        readonly IDeviceLiveness _liveness;

        // The suballocation each live block came out of, which VulkanStagingBlock deliberately does not carry: it
        // is the arena's value type and the arena has no allocator to hand one back to.
        readonly object _gate = new();
        readonly Dictionary<ulong, VulkanMemoryAllocation> _blocks = new();

        // INTERLOCKED RATHER THAN UNDER _gate (https://github.com/APKiwiOrg/KhaozEngine/issues/551). Both counters
        // are incremented AFTER the ledger lock is released, so an auto-property increment was a plain read, add
        // and write that two arenas destroying at once could interleave and lose. Taking the gate again for them
        // would put a second lock acquisition on every destroy, and the deferred arm would still have to drop it
        // before calling out to the retire list, so the atomic is both the cheaper answer and the narrower one.
        int _deferredDestroys;
        int _abandonedDestroys;

        /// <param name="owner">The device's resource seam, allocator, timeline and retire list.</param>
        /// <param name="liveness">The device's liveness token, which decides between deferring and
        /// abandoning.</param>
        internal VulkanStagingSource(VulkanResourceOwner owner, IDeviceLiveness liveness)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(liveness);

            _owner = owner;
            _liveness = liveness;
        }

        /// <summary>How many blocks this source has created and not yet handed to the retire list. A reading for
        /// the tests that prove the deferral, and for a leak that would otherwise be invisible.</summary>
        internal int LiveBlockCount
        {
            get { lock (_gate) return _blocks.Count; }
        }

        /// <summary>How many destroys were DEFERRED through the retire list rather than made immediately. The
        /// number the contract is about.</summary>
        internal int DeferredDestroyCount => Volatile.Read(ref _deferredDestroys);

        /// <summary>How many destroys were ABANDONED because the device was dead. Reported rather than silent: a
        /// large number says a consumer was still recording uploads after the device had gone.</summary>
        internal int AbandonedDestroyCount => Volatile.Read(ref _abandonedDestroys);

        /// <inheritdoc/>
        public VulkanStagingBlock Create(ulong sizeBytes)
        {
            ArgumentOutOfRangeException.ThrowIfZero(sizeBytes);

            ulong buffer = _owner.Api.CreateBuffer(
                sizeBytes, VulkanBufferBinding.TransferSrc | VulkanBufferBinding.TransferDst);

            VulkanMemoryAllocation allocation = default;
            try
            {
                VulkanResourceRequirements requirements = _owner.Api.BufferRequirements(buffer);

                allocation = _owner.Memory.Allocate(new VulkanMemoryRequest(
                    requirements.Size,
                    requirements.Alignment,
                    requirements.MemoryTypeBits,
                    // UPLOAD: host-visible and coherent, preferably not device-local, because bytes on their way
                    // to VRAM should not be occupying VRAM.
                    VulkanMemoryUsage.Upload,
                    VulkanMemoryTiling.Linear,
                    requirements.PrefersDedicated,
                    requirements.RequiresDedicated,
                    new VulkanDedicatedTarget(Buffer: buffer, Image: 0)));

                _owner.Api.BindBufferMemory(buffer, allocation.Memory, allocation.Offset);

                nint mapped = allocation.MappedPointer;
                if (mapped == 0)
                {
                    throw new InvalidOperationException(
                        "A native Vulkan staging block of "
                        + sizeBytes.ToString(CultureInfo.InvariantCulture)
                        + " bytes was allocated out of a chunk with no mapping. The upload ladder's every rung is "
                        + "host-visible and host-visible chunks are mapped once at creation and never unmapped "
                        + "(V-M3), so an unmapped block means the ladder returned a device-local type.");
                }

                lock (_gate) _blocks[buffer] = allocation;

                return new VulkanStagingBlock(buffer, mapped, sizeBytes);
            }
            catch
            {
                // Nothing was ever submitted against a block that failed to finish being built, so there is no
                // work to defer behind: destroyed immediately rather than retired.
                if (allocation.IsValid) _owner.Memory.Free(allocation);
                _owner.Api.DestroyBuffer(buffer);
                throw;
            }
        }

        /// <inheritdoc/>
        public void Destroy(in VulkanStagingBlock block)
        {
            if (block.Buffer == 0) return;

            VulkanMemoryAllocation allocation;
            lock (_gate)
            {
                if (!_blocks.Remove(block.Buffer, out allocation)) return;
            }

            if (_liveness.IsDead)
            {
                Interlocked.Increment(ref _abandonedDestroys);
                return;
            }

            ulong buffer = block.Buffer;
            VulkanResourceOwner owner = _owner;

            Interlocked.Increment(ref _deferredDestroys);
            owner.RetireTerminal(() =>
            {
                owner.Api.DestroyBuffer(buffer);
                if (allocation.IsValid) owner.Memory.Free(allocation);
            });
        }
    }
}
