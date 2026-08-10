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
    /// <para><b>IT IS NOT RING-BACKED YET AND THE PREDICATE THAT WILL DECIDE THAT IS ALREADY HERE.</b>
    /// <see cref="IsRingBacked"/> answers M-M6's first invariant today, and the uniform ring row
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/574) is what turns a ring-backed buffer's allocation into
    /// <c>stride * FramesInFlight</c> and adds the segment gate. Until then a uniform buffer is a plain Shared
    /// buffer of the requested size, which is exactly what the incumbent creates, so the behaviour is the
    /// incumbent's rather than a half-built ring.</para>
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

        bool _disposed;

        MetalBuffer(IMetalDeviceLiveness liveness, MTLBuffer buffer, IntPtr contents, uint sizeInBytes,
            GpuBufferUsage usage)
        {
            _liveness = liveness;
            _buffer = buffer;
            _contents = contents;
            SizeInBytes = sizeInBytes;
            Usage = usage;
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
        /// Create one, or throw by name. Refuses M-M6's illegal combination FIRST, so a caller that asked for the
        /// impossible gets the reason rather than an allocation it then cannot use.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static MetalBuffer Create(MTLDevice device, IMetalDeviceLiveness liveness,
            in GpuBufferDescription description)
        {
            MetalBufferPolicy.RequireCreatable(description.Usage);

            if (description.SizeInBytes == 0)
            {
                throw new ArgumentException(
                    "A native Metal buffer needs a non-zero size: -newBufferWithLength:options: refuses a zero "
                    + "length and answers nil, which would surface later as a nil resource bound to a shader "
                    + "rather than here.", nameof(description));
            }

            uint allocation = MetalBufferPolicy.AllocationBytes(description.SizeInBytes);

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

            return new MetalBuffer(liveness, buffer, contents, description.SizeInBytes, description.Usage);
        }

        /// <summary>
        /// Copy <paramref name="source"/> into this buffer at <paramref name="offsetBytes"/>, which is the whole
        /// of a device-level <c>UpdateBuffer</c> on this backend: every buffer is Shared, so there is no staging
        /// buffer, no blit and no command buffer.
        ///
        /// <para><b>THIS WRITE IS UNGATED, EXACTLY AS THE INCUMBENT'S IS, AND M-M7 SAYS THAT IS A RACE.</b>
        /// <c>MTLGraphicsDevice.UpdateBufferCore</c> is an <c>Unsafe.CopyBlock</c> into <c>contents()</c> with no
        /// fence, no frame index and no diagnostic, and Metal renames nothing under a write, so a submitted
        /// command buffer reading those bytes reads whatever the CPU has got to. What closes it is the uniform
        /// ring's completion gate, which is the ring row's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/574) and which reaches every segment under #484's
        /// rule. Until that row lands this reproduces the incumbent's behaviour, which is the bar this row is held
        /// to, and the gap is named here rather than left for a reader to find.</para>
        /// </summary>
        [SupportedOSPlatform("macos")]
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal unsafe void Write(uint offsetBytes, ReadOnlySpan<byte> source)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            MetalBufferPolicy.RequireWriteFits(offsetBytes, (uint)source.Length, SizeInBytes);

            if (source.IsEmpty) return;
            if (_liveness.IsDead) return;

            source.CopyTo(new Span<byte>((byte*)_contents + offsetBytes, source.Length));
        }

        /// <summary>
        /// The buffer's bytes as a span, for the <c>Map</c> path. The whole REQUESTED size rather than the rounded
        /// allocation, because the rounding is a padding the caller never asked for and never owns.
        /// </summary>
        internal MappedData Mapped() => new(Contents, SizeInBytes, SizeInBytes);

        /// <summary>
        /// Release the buffer, once, and never on a dead device (M-F6).
        /// <para>
        /// NO RETIRE LIST AND NO DEFERRAL (M-H3). A submitted command buffer retains what it references, so this
        /// is safe with work in flight, which is the property that makes the Vulkan sibling's deferred-disposal
        /// machinery unnecessary here rather than merely absent.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

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
