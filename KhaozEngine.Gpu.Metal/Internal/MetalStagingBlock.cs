using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// ONE BLOCK OF A <see cref="MetalStagingArena"/>: a Shared <c>MTLBuffer</c> and the <c>contents()</c>
    /// pointer taken with it, as plain numbers.
    /// <para>
    /// THE HANDLE IS AN <see cref="IntPtr"/> RATHER THAN AN <c>MTLBuffer</c>, which is what keeps the arena
    /// itself device-free: a fake source invents a pinned array and a number, and the size classes, the
    /// sub-allocation, the recycling boundary and the retention cap all run under a plain <c>[Fact]</c> on a
    /// machine with no Metal at all. That is the same split <see cref="IMetalSetupNative"/> and
    /// <see cref="IMetalSharedEvent"/> take.
    /// </para>
    /// </summary>
    /// <param name="Buffer">The <c>MTLBuffer</c> handle, at +1, owned by the source that made it.</param>
    /// <param name="Mapped">Its <c>contents()</c> pointer, stable for the buffer's life (M-M2).</param>
    /// <param name="SizeBytes">How many bytes it holds.</param>
    internal readonly record struct MetalStagingBlock(IntPtr Buffer, IntPtr Mapped, ulong SizeBytes)
    {
        /// <summary>Whether this is a real block. A source that answered a null buffer or a null pointer has
        /// failed, and the arena refuses it by name rather than sub-allocating into address zero.</summary>
        internal bool IsValid => Buffer != IntPtr.Zero && Mapped != IntPtr.Zero && SizeBytes > 0;
    }

    /// <summary>
    /// A SUB-ALLOCATION INSIDE ONE BLOCK: where a caller writes its payload, and the pair of numbers the blit
    /// copy needs to name it.
    /// <para>
    /// THE OFFSET AND THE POINTER ARE BOTH CARRIED because they are used by different halves. The CPU writes
    /// through <see cref="Mapped"/>, and <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c>
    /// takes the buffer handle plus <see cref="OffsetBytes"/>, since a blit names a buffer and an offset rather
    /// than an address.
    /// </para>
    /// </summary>
    /// <param name="Buffer">The block's <c>MTLBuffer</c> handle, which the copy's source is.</param>
    /// <param name="OffsetBytes">Where in that buffer this lease starts.</param>
    /// <param name="Mapped">The CPU address of that same byte.</param>
    /// <param name="SizeBytes">How many bytes the lease covers.</param>
    internal readonly record struct MetalStagingLease(
        IntPtr Buffer, ulong OffsetBytes, IntPtr Mapped, ulong SizeBytes)
    {
        /// <summary>
        /// Whether this lease names memory. False for the one lease the arena hands back without leasing
        /// anything, which is what a request on a DEAD device answers (<see cref="MetalStagingArena.Take"/>).
        /// <para>
        /// A CALLER MUST ASK BEFORE TOUCHING <see cref="Span"/>, because that span is built over
        /// <see cref="Mapped"/> and an invalid lease's is null. This is the same question
        /// <see cref="MetalStagingBlock.IsValid"/> asks one level down, and it is a question rather than a throw
        /// for the reason the whole dead-device posture is: a device that has gone makes every later call a
        /// no-op rather than an exception, because the seam has no recovery path and the frame loop above it is
        /// not written to handle one.
        /// </para>
        /// </summary>
        internal bool IsValid => Buffer != IntPtr.Zero && Mapped != IntPtr.Zero && SizeBytes > 0;

        /// <summary>The lease's bytes, for the caller to copy its payload into. Only on a lease that
        /// <see cref="IsValid"/>.</summary>
        internal unsafe Span<byte> Span => new((byte*)Mapped, (int)SizeBytes);
    }
}
