using System;
using System.Runtime.InteropServices;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE SEAM'S TWO RECORD-TIME <c>UpdateBuffer</c> OVERLOADS, resolving the buffer and handing the decision to
    /// <see cref="MetalBufferUpload"/>.
    ///
    /// <para><b>THE DECISION IS NOT HERE ON PURPOSE.</b> Which path a write takes, what the staging pad does and
    /// what the arena is asked for are all things that can be WRONG in a way no golden can see (a uniform write
    /// routed through the staging path renders identical pixels and costs a full state re-activation), and
    /// nothing about them needs an <c>MTLDevice</c>. What is left here is the two things that DO need one: the
    /// buffer's identity and the recording state.</para>
    ///
    /// <para><b>A SEPARATE PARTIAL because the lifecycle in <c>MetalCommandList.cs</c> is the part every later
    /// row reads before adding to it</b>, and the design's own KESIZE warning for this phase is that the
    /// incumbent's <c>MTLCommandList.cs</c> is 1163 lines against an 800-line cap. The split is made where the
    /// concern changes rather than at whatever line the cap is reached, which is what the ratchet is for.</para>
    /// </summary>
    internal sealed partial class MetalCommandList
    {
        /// <summary>
        /// This list's staging arena (M-M8). Exposed because a test reads its counters, which is the only way
        /// "pooled by size" is observable at all, and because the arena is created by the device and disposed by
        /// this list.
        /// </summary>
        internal MetalStagingArena Arena => _arena;

        /// <inheritdoc/>
        /// <remarks>The single-value overload, which is what every per-frame uniform write in the engine uses. It
        /// reaches exactly the same routing as the span overload, through the same one implementation, so the two
        /// cannot drift into taking different paths for the same buffer.</remarks>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => UpdateBufferCore(b, offsetBytes, MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in data)));

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => UpdateBufferCore(b, offsetBytes, MemoryMarshal.AsBytes(data));

        void UpdateBufferCore(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);

            // THE OWNER CHECK IS ARGUMENT VALIDATION and comes first, in row 6's shape rather than a third
            // mechanism: a caller passing another device's buffer has made the same mistake whether or not this
            // list is recording. The LIST is checked by owner token at the submit, which is row 7's surface, and
            // the two stay separate because they answer about different things.
            MetalBuffer metal = MetalResourceOwnership.Require<MetalBuffer>(buffer, _liveness, nameof(buffer));

            if (!_recording)
            {
                throw new InvalidOperationException(
                    "UpdateBuffer was called on a native Metal command list that is not recording. Call Begin "
                    + "first. A record-time upload lands at the point in the command stream where it was "
                    + "recorded, which is the entire difference from the device-level overload of the same name, "
                    + "and there is no stream to record it into yet.");
            }

            // THE SEGMENT IS THIS RECORDING'S OWN, captured at Begin (see MetalCommandList.RingSegment). Reading
            // the allocator's current segment here instead would let another list's Begin move it mid-recording.
            MetalBufferUpload.Record(metal.Ring, _segment, metal.Handle.Handle, metal.SizeInBytes, offsetBytes,
                data, _encoders, _arena, _blit);
        }
    }
}
