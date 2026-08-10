using System;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using KhaozEngine.Gpu.Metal.Internal.ObjC;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S <see cref="IGpuBuffer"/> OVER ONE Shared <c>MTLBuffer</c>, persistently CPU-visible.
    ///
    /// <para><b>SHARED, ALWAYS, WHICH IS DECISION M-M2 AND ALSO THE INCUMBENT'S ONLY CHOICE.</b> On unified memory
    /// the CPU and the GPU address the same pages, so a write is a write and there is no staging path, no flush
    /// and no invalidate. <see cref="Contents"/> is taken ONCE at creation and kept: the pointer is stable for the
    /// buffer's life by Metal's own contract, and taking it per write would be a message send on the uniform path
    /// the ring row exists to make free.</para>
    ///
    /// <para><b>THE ALLOCATION IS ROUNDED UP TO A MULTIPLE OF FOUR AND THE REPORTED SIZE IS NOT</b>
    /// (<see cref="MetalBufferPolicy.AllocationBytes"/>), which is <c>Veldrid.MTL.MTLBuffer</c>'s own split
    /// between <c>ActualCapacity</c> and <c>SizeInBytes</c>. See that policy for why the rounding is reached
    /// rather than theoretical.</para>
    ///
    /// <para><b>A UNIFORM BUFFER IS RING-BACKED AND EVERY OTHER BUFFER IS NOT (M-M6).</b>
    /// <see cref="IsRingBacked"/> is the creation-time predicate, and a buffer that answers it is allocated at
    /// <c>stride * FramesInFlight</c> rather than at the requested size, with a <see cref="MetalUniformRing"/>
    /// over it. <see cref="IGpuBuffer.SizeInBytes"/> still reports what the CALLER asked for, which is what makes
    /// the whole ring invisible through the seam: one buffer identity, one logical size, and the frame base
    /// applied at BIND.</para>
    ///
    /// <para><b>DISPOSAL AFTER DEVICE DEATH IS A NO-OP (M-F6), and disposal WHILE THE GPU IS READING IS SAFE
    /// (M-H3).</b> The first is the liveness token every wrapper carries. The second is Metal's own object model:
    /// an <c>MTLCommandBuffer</c> retains every resource its encoders reference until it completes, so releasing
    /// here drops the application's reference and the driver keeps the allocation alive as long as it needs it.
    /// That is what removes the retire list the Vulkan sibling needs, and it is why this type has no deferred
    /// disposal of any kind.</para>
    /// </summary>
    internal sealed class MetalBuffer : IGpuBuffer, IMetalOwnedResource
    {
        readonly IMetalDeviceLiveness _liveness;
        readonly MTLBuffer _buffer;
        readonly IntPtr _contents;
        readonly MetalRingAllocator? _rings;
        readonly MetalUniformRing? _ring;

        bool _disposed;

        MetalBuffer(IMetalDeviceLiveness liveness, MTLBuffer buffer, IntPtr contents, uint sizeInBytes,
            GpuBufferUsage usage, MetalRingAllocator? rings)
        {
            _liveness = liveness;
            _buffer = buffer;
            _contents = contents;
            SizeInBytes = sizeInBytes;
            Usage = usage;

            if (rings is null || !MetalBufferPolicy.IsRingBacked(usage)) return;

            _rings = rings;
            _ring = new MetalUniformRing(rings, contents, sizeInBytes);
        }

        /// <inheritdoc/>
        /// <remarks>The size the CALLER asked for, never the rounded allocation. See
        /// <see cref="MetalBufferPolicy.AllocationBytes"/>.</remarks>
        public uint SizeInBytes { get; }

        /// <summary>The usage the buffer was created with, which is what a bind and the ring row read.</summary>
        internal GpuBufferUsage Usage { get; }

        /// <inheritdoc/>
        public IMetalDeviceLiveness Owner => _liveness;

        /// <summary>The native buffer, for the rows that bind and copy. Null after disposal, deliberately: a
        /// caller reaching a disposed buffer gets a nil handle Metal rejects rather than a released pointer it
        /// dereferences.</summary>
        internal MTLBuffer Handle => _disposed ? default : _buffer;

        /// <summary>The persistently mapped base pointer, taken once at creation.</summary>
        internal IntPtr Contents => _disposed ? IntPtr.Zero : _contents;

        /// <summary>Whether this buffer is the ring's to rebase (M-M6). False for everything the engine binds by
        /// hand.</summary>
        internal bool IsRingBacked => MetalBufferPolicy.IsRingBacked(Usage);

        /// <summary>
        /// The uniform ring behind this buffer, or null for every buffer that is not
        /// <see cref="IsRingBacked"/>, and null for a ring-backed buffer created without an allocator, which is
        /// the shape only a test builds. Read by the device-level write (M-M5) and by the record-time one.
        /// </summary>
        internal MetalUniformRing? Ring => _ring;

        /// <summary>
        /// Create one, or throw by name. Refuses M-M6's illegal combination FIRST, so a caller that asked for the
        /// impossible gets the reason rather than an allocation it then cannot use.
        /// </summary>
        /// <param name="device">The device to allocate on.</param>
        /// <param name="liveness">The device's liveness token (M-F6).</param>
        /// <param name="description">What the seam asked for.</param>
        /// <param name="rings">The device's ring allocator, which a UniformBuffer-usage buffer is cut into
        /// segments against (M-M3). Null leaves even a uniform buffer a plain Shared buffer of the requested
        /// size, which is the incumbent's shape and is what a device built before the ring existed would
        /// give.</param>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalBuffer Create(MTLDevice device, IMetalDeviceLiveness liveness,
            in GpuBufferDescription description, MetalRingAllocator? rings)
        {
            MetalBufferPolicy.RequireCreatable(description.Usage);

            if (description.SizeInBytes == 0)
            {
                throw new ArgumentException(
                    "A native Metal buffer needs a non-zero size: -newBufferWithLength:options: refuses a zero "
                    + "length and answers nil, which would surface later as a nil resource bound to a shader "
                    + "rather than here.", nameof(description));
            }

            // THE RING IS WHAT CHANGES THE SIZE, and nothing else about the allocation. A ring-backed buffer is
            // ONE MTLBuffer of stride * FramesInFlight (M-M3), so the seam's caller gets one identity and one
            // logical size while the frame base rides the bind. Everything else takes the incumbent's four-byte
            // rounding, which the ring's 256-byte stride already subsumes.
            nuint allocation = rings is not null && MetalBufferPolicy.IsRingBacked(description.Usage)
                ? RingAllocationBytes(description.SizeInBytes, rings.FramesInFlight)
                : MetalBufferPolicy.AllocationBytes(description.SizeInBytes);

            MTLBuffer buffer = device.NewBuffer(allocation, MTLResourceOptions.SharedDefaultCache);
            if (buffer.IsNull)
            {
                throw new InvalidOperationException(
                    "The native Metal device would not allocate a buffer of " + allocation
                    + " bytes. -newBufferWithLength:options: answers nil only when the allocation itself fails, "
                    + "so this is memory pressure rather than a malformed request.");
            }

            // ONCE. Metal documents the pointer as stable for the buffer's life, and the uniform ring's whole
            // saving is that a record-time write is a memcpy with no message send in front of it.
            IntPtr contents = buffer.Contents();
            if (contents == IntPtr.Zero)
            {
                buffer.Release();
                throw new InvalidOperationException(
                    "A Shared MTLBuffer answered a null -contents pointer, which cannot happen for the storage "
                    + "mode this backend creates every buffer with. Something has changed about the storage mode "
                    + "rather than about this allocation.");
            }

            return new MetalBuffer(liveness, buffer, contents, description.SizeInBytes, description.Usage, rings);
        }

        /// <summary>
        /// The whole allocation a ring-backed buffer of <paramref name="sizeInBytes"/> takes across
        /// <paramref name="framesInFlight"/> segments, refused by name when it would leave the 32-bit range the
        /// seam carries buffer sizes in.
        /// <para>
        /// THE MESSAGE NAMES THE KNOB, because the lever is real: the total is the stride times the depth, and
        /// <c>KE_METAL_FRAMES_IN_FLIGHT</c> is the only term a caller can lower without changing their own
        /// buffer. Reaching this at the default three needs a uniform buffer well past a gigabyte, which is not
        /// a uniform buffer.
        /// </para>
        /// </summary>
        internal static nuint RingAllocationBytes(uint sizeInBytes, int framesInFlight)
        {
            ulong total = MetalRingStride.TotalBytesFor(sizeInBytes, framesInFlight);

            if (total > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "A ring-backed native Metal uniform buffer of " + sizeInBytes + " bytes across "
                    + framesInFlight + " frame segments needs " + total
                    + " bytes, which leaves the 32-bit size the GPU seam carries buffer sizes in. Lower "
                    + MetalFramesInFlight.EnvVarName + " or split the buffer.");
            }

            return (nuint)total;
        }

        /// <summary>
        /// Copy <paramref name="source"/> into this buffer at <paramref name="offsetBytes"/>, which is the whole
        /// of a device-level <c>UpdateBuffer</c> on this backend: every buffer is Shared, so there is no staging
        /// buffer, no blit and no command buffer.
        ///
        /// <para><b>A RING-BACKED BUFFER NEVER COMES HERE, AND THAT REFUSAL IS M-M7.</b>
        /// <c>MTLGraphicsDevice.UpdateBufferCore</c> is an <c>Unsafe.CopyBlock</c> into <c>contents()</c> with no
        /// fence, no frame index and no diagnostic, and Metal renames nothing under a write, so a submitted
        /// command buffer reading those bytes reads whatever the CPU has got to. That is a plain data race in
        /// shipped code, and what closes it is <see cref="MetalRingAllocator.UpdateBuffer"/>'s completion gate,
        /// which reaches every segment under
        /// https://github.com/APKiwiOrg/KhaozEngine/issues/484's rule. So a uniform buffer's device-level write
        /// routes there and this member throws rather than quietly reproducing the race for a caller that reached
        /// it by another road.</para>
        ///
        /// <para><b>EVERY OTHER BUFFER IS STILL AN UNGATED COPY, AND THAT IS THE INCUMBENT'S BEHAVIOUR
        /// UNCHANGED.</b> A device-level write to a vertex, index or structured buffer is a load-time call by
        /// construction, the ring is deliberately only for uniforms (M-M6), and nothing on this seam gates it on
        /// either sibling backend either.</para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void Write(uint offsetBytes, ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_ring is not null)
            {
                throw new InvalidOperationException(
                    "A ring-backed native Metal uniform buffer was written through the ungated buffer copy. Its "
                    + "device-level write goes through the ring allocator, which reaches EVERY segment and gates "
                    + "each one on the completion value that segment's frame was closed at (M-M5, M-M7). Copying "
                    + "straight into contents() here would reach one segment out of the frames in flight AND "
                    + "would race a submitted command buffer reading it, which is the pair of defects the ring "
                    + "exists to close.");
            }

            MetalBufferPolicy.RequireWriteFits(offsetBytes, (uint)source.Length, SizeInBytes);

            if (source.IsEmpty) return;
            if (_liveness.IsDead) return;

            source.CopyTo(new Span<byte>((byte*)_contents + offsetBytes, source.Length));
        }

        /// <summary>
        /// The buffer's bytes as a span, for the <c>Map</c> path. The whole REQUESTED size rather than the rounded
        /// allocation, because the rounding is a padding the caller never asked for and never owns.
        /// <para>
        /// A RING-BACKED BUFFER MAPS ITS CURRENT SEGMENT, which is the only answer that means anything: the
        /// caller asked for a buffer of <see cref="SizeInBytes"/> bytes and the segment IS that buffer as far as
        /// the seam is concerned, so handing back the whole allocation would hand back N copies with no way to
        /// tell which one the next submit binds. It is also the answer a readback wants, since a record-time
        /// write goes to exactly this segment.
        /// </para>
        /// </summary>
        internal MappedData Mapped()
        {
            if (_ring is null) return new MappedData(Contents, SizeInBytes, SizeInBytes);

            IntPtr contents = Contents;
            IntPtr segment = contents == IntPtr.Zero
                ? IntPtr.Zero
                : contents + (nint)_ring.CurrentFrameBaseBytes;

            return new MappedData(segment, SizeInBytes, SizeInBytes);
        }

        /// <summary>
        /// Release the buffer, once, and never on a dead device (M-F6).
        /// <para>
        /// NO RETIRE LIST AND NO DEFERRAL (M-H3). A submitted command buffer retains what it references, so this
        /// is safe with work in flight, which is the property that makes the Vulkan sibling's deferred-disposal
        /// machinery unnecessary here rather than merely absent.
        /// </para>
        /// <para>
        /// A RING-BACKED BUFFER LEAVES THE ALLOCATOR FIRST, AND REFERENCE COUNTING DOES NOT COVER THAT ONE. What
        /// Objective-C keeps alive is the ALLOCATION, for as long as a submitted command buffer references it. A
        /// pending off-timeline patch is a CPU write scheduled for a future frame boundary, so leaving one queued
        /// would write through this buffer's <c>contents()</c> pointer after the engine dropped its own
        /// reference. <see cref="MetalRingAllocator.Forget"/> takes it out under the submit lock and counts the
        /// dropped patches on the way.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // BEFORE the liveness check, deliberately. The patches are managed state and dropping them is
            // correct on a dead device too, where the release below is skipped: a queued patch on a device that
            // has gone would be replayed by any later frame boundary into memory nothing owns.
            if (_ring is not null) _rings?.Forget(_ring);

            if (_liveness.IsDead) return;
            if (!KhaozEngineMetal.IsPlatformSupported) return;

            ReleaseOnMacOs();
        }

        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        void ReleaseOnMacOs()
        {
            using ObjCAutoreleasePool pool = ObjCAutoreleasePool.Enter();
            _buffer.Release();
        }
    }
}
