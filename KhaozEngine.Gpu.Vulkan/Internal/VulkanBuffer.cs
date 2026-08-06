using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// <see cref="IGpuBuffer"/> on the native Vulkan backend: one <c>VkBuffer</c>, one suballocation out of the
    /// device's block allocator, and (for a uniform buffer) the ring cut into it.
    ///
    /// <para><b>A UNIFORM BUFFER IS RING-BACKED AND IS <see cref="VulkanFramesInFlight"/> TIMES LARGER THAN THE
    /// SEAM ASKED FOR (V-M5, 9.2).</b> <see cref="SizeInBytes"/> stays the LOGICAL size, which is the only size a
    /// caller ever sees or reasons about, and the native buffer holds one segment per frame in flight at the ring's
    /// stride. The identity never changes and the frame's base is applied at the BIND, as the dynamic uniform
    /// descriptor's <c>pDynamicOffsets</c> entry, so a resource set built once at load time still names the same
    /// handle and the same logical offset every frame.</para>
    ///
    /// <para><b>THE RING DECISION IS <see cref="VulkanBufferRingPolicy.ForBuffer"/>'s AND IT IS ASKED BEFORE
    /// ANYTHING IS ALLOCATED.</b> It also refuses the one combination this backend cannot honour, a uniform buffer
    /// that is also bound some other way, which is a documented divergence from the Veldrid leg rather than a
    /// defect. See that type for the whole argument.</para>
    ///
    /// <para><b>MEMORY COMES OFF THE LADDER <see cref="VulkanViewPolicy.MemoryFor"/> CHOOSES</b>: the ring's
    /// coherent host-visible type with no fallback for a uniform buffer, the cached readback type for a staging
    /// buffer, and device-local for everything else. A staging buffer is therefore mappable and everything else is
    /// not, which is what makes <see cref="MappedPointer"/> non-zero exactly where the seam permits a
    /// <c>Map</c>.</para>
    ///
    /// <para><b>DISPOSAL IS ONE TERMINAL RETIRE (V-F9).</b> One entry destroys the <c>VkBuffer</c> and frees the
    /// suballocation, in that order, once the timeline has passed the value recorded here. See
    /// <see cref="VulkanResourceOwner.RetireTerminal"/> for why nothing here re-retires anything.</para>
    /// </summary>
    internal sealed class VulkanBuffer : IGpuBuffer, IVulkanRingBacked, IVulkanUploadDestination
    {
        readonly VulkanResourceOwner _owner;
        readonly VulkanRingAllocator? _rings;
        readonly GpuBufferUsage _usage;
        readonly VulkanMemoryAllocation _allocation;

        bool _disposed;

        /// <param name="owner">The device's resource seam, allocator, timeline and retire list.</param>
        /// <param name="rings">The device's ring allocator, or null on a device with no ring. A uniform buffer
        /// needs one and refuses to be built without it, because a ring-backed buffer with no ring would be a
        /// buffer whose bind base nothing supplies.</param>
        /// <param name="description">The seam's description, whose size is the LOGICAL size.</param>
        /// <param name="minUniformBufferOffsetAlignment">The device limit the ring stride is rounded to. Ignored
        /// for a buffer that is not ring-backed.</param>
        internal VulkanBuffer(VulkanResourceOwner owner, VulkanRingAllocator? rings,
            in GpuBufferDescription description,
            ulong minUniformBufferOffsetAlignment = VulkanRingStride.OffsetAlignmentFloor)
        {
            ArgumentNullException.ThrowIfNull(owner);

            // THE FIRST STATEMENT, before a single byte is allocated (V-M7). It either throws on the combination
            // this backend refuses or answers whether the native buffer is one segment or N.
            bool ringBacked = VulkanBufferRingPolicy.ForBuffer(description.Usage);

            if (description.SizeInBytes == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(description), description.SizeInBytes,
                    "A native Vulkan buffer of zero bytes cannot be created: vkCreateBuffer rejects a size of 0.");
            }

            if (ringBacked && rings is null)
            {
                throw new ArgumentNullException(nameof(rings),
                    "A ring-backed uniform buffer needs the device's ring allocator, and none was supplied. Its "
                    + "segment index, its completion gate and its off-timeline patch queue all live there, so a "
                    + "buffer built without one would have no base to bind at and no way to be written "
                    + "off-timeline.");
            }

            _owner = owner;
            _rings = ringBacked ? rings : null;
            _usage = description.Usage;

            SizeInBytes = description.SizeInBytes;

            ulong nativeBytes = ringBacked
                ? VulkanRingStride.TotalBytesFor(
                    description.SizeInBytes, rings!.FramesInFlight, minUniformBufferOffsetAlignment)
                : description.SizeInBytes;

            Handle = owner.Api.CreateBuffer(nativeBytes, VulkanViewPolicy.ForBuffer(description.Usage));

            try
            {
                VulkanResourceRequirements requirements = owner.Api.BufferRequirements(Handle);

                _allocation = owner.Memory.Allocate(new VulkanMemoryRequest(
                    requirements.Size,
                    requirements.Alignment,
                    requirements.MemoryTypeBits,
                    VulkanViewPolicy.MemoryFor(description.Usage),
                    // LINEAR, always. A buffer is a linear resource by definition, and the pool key's second half
                    // is what keeps it out of any chunk holding an optimal-tiled image, which is this allocator's
                    // whole bufferImageGranularity implementation (V-M2).
                    VulkanMemoryTiling.Linear,
                    requirements.PrefersDedicated,
                    requirements.RequiresDedicated,
                    new VulkanDedicatedTarget(Buffer: Handle, Image: 0)));

                owner.Api.BindBufferMemory(Handle, _allocation.Memory, _allocation.Offset);

                if (ringBacked)
                {
                    Ring = new VulkanUniformRing(rings!, _allocation.MappedPointer, description.SizeInBytes,
                        minUniformBufferOffsetAlignment);
                }
            }
            catch
            {
                // Between vkCreateBuffer and the last assignment this constructor holds native objects nothing
                // else knows about, so a throw in the middle would leak them for the process's life. Destroyed
                // immediately rather than retired: nothing was ever submitted against a buffer that failed to
                // finish being built, so there is no work to wait behind.
                if (_allocation.IsValid) owner.Memory.Free(_allocation);
                owner.Api.DestroyBuffer(Handle);
                throw;
            }
        }

        /// <inheritdoc/>
        /// <remarks>The LOGICAL size. A ring-backed uniform buffer's native allocation is
        /// <see cref="VulkanFramesInFlight"/> segments of at least this, and no caller sees that number.</remarks>
        public uint SizeInBytes { get; }

        /// <summary>The <c>VkBuffer</c> handle, which a bind, a copy and a descriptor all name.</summary>
        internal ulong Handle { get; }

        /// <inheritdoc/>
        /// <remarks>Non-null for exactly the uniform buffers, which
        /// <see cref="VulkanBufferRingPolicy.ForBuffer"/> makes an exclusive set: a ring-backed buffer can never
        /// also be a vertex, index, indirect or storage buffer whose bind would address segment zero.</remarks>
        public VulkanUniformRing? Ring { get; }

        /// <inheritdoc/>
        ulong IVulkanUploadDestination.DeviceBuffer => Handle;

        /// <inheritdoc/>
        GpuBufferUsage IVulkanUploadDestination.UploadUsage => _usage;

        /// <summary>The usage the seam asked for, for the diagnostics that have to say what a buffer is.</summary>
        internal GpuBufferUsage Usage => _usage;

        /// <summary>Whether this buffer is CPU-mappable through the seam, which is the staging bit and nothing
        /// else. A dynamic buffer is NOT mappable here: see <see cref="VulkanViewPolicy.MemoryFor"/>.</summary>
        internal bool IsStaging => (_usage & GpuBufferUsage.Staging) != 0;

        /// <summary>True once disposed, whether or not the deferred destroy has run yet.</summary>
        internal bool IsDisposed => _disposed;

        /// <summary>
        /// The mapped address of the buffer's first byte, or zero when its memory is not host-visible. Stable for
        /// the buffer's whole life (V-M3): host-visible chunks are mapped once at creation and never unmapped, so
        /// a staging <c>Map</c> is a pointer read rather than a <c>vkMapMemory</c>.
        /// </summary>
        internal nint MappedPointer => _allocation.MappedPointer;

        /// <summary>The suballocation, which the readback path invalidates before handing a pointer out on a
        /// non-coherent memory type.</summary>
        internal VulkanMemoryAllocation Allocation => _allocation;

        /// <summary>
        /// Retire the buffer behind the timeline (V-F9). ONE terminal entry: destroy the <c>VkBuffer</c>, then free
        /// its suballocation.
        /// <para>
        /// THE RING IS FORGOTTEN IMMEDIATELY rather than at the deferred destroy, because a pending off-timeline
        /// patch is HOST memory queued against a segment: replaying it after this buffer's mapping has gone would
        /// be a write through a freed pointer, and dropping it now costs a value nobody can read any more.
        /// </para>
        /// <para>
        /// IDEMPOTENT, because a consumer disposing a buffer twice is a teardown-order accident rather than a
        /// defect, and retiring the same handle twice would double-destroy it.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (Ring is not null) _rings?.Forget(Ring);

            ulong handle = Handle;
            VulkanMemoryAllocation allocation = _allocation;
            VulkanResourceOwner owner = _owner;

            owner.RetireTerminal(() =>
            {
                owner.Api.DestroyBuffer(handle);
                if (allocation.IsValid) owner.Memory.Free(allocation);
            });
        }

        /// <summary>The diagnostic line a refusal quotes, so a message about the wrong kind of buffer says which
        /// kind it got.</summary>
        internal string Describe()
            => SizeInBytes.ToString(CultureInfo.InvariantCulture) + "-byte " + _usage + " buffer";
    }
}
