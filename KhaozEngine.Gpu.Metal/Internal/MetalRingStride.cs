using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE RING'S ARITHMETIC, AND THE ONE INVARIANT LEFT AFTER BOTH NEIGHBOURS' COLLAPSED. Decisions M-M3 and
    /// M-M4, section 9.2.
    ///
    /// <para><b>THE STRIDE IS THE SPACING OF THE SEGMENTS.</b> A ring-backed uniform buffer is ONE
    /// <c>MTLBuffer</c> of <c>stride * FramesInFlight</c> bytes, where <c>stride = align(size, 256)</c>, and the
    /// frame base is applied AT BIND through <c>setVertexBufferOffset:</c> or its stage sibling (M-R7). There is
    /// no descriptor, so there is no second number to keep the stride apart from.</para>
    ///
    /// <para><b>256 IS A FLOOR THIS BACKEND CHOOSES OVER A DEVICE NUMBER IT COULD HAVE READ, WHICH IS THE
    /// DIFFERENCE FROM BOTH SIBLINGS (M-M3).</b> The incumbent's <c>GetUniformBufferMinOffsetAlignmentCore</c>
    /// answers <c>MetalFeatures.IsMacOS ? 16u : 256u</c>, so a device-derived stride would pack tighter here and
    /// the Vulkan sibling has to take <c>max(256, minUniformBufferOffsetAlignment)</c> because its limit can in
    /// principle be larger. Three reasons to floor flat at 256 instead. The seam already documents 256 as the
    /// safe alignment across all three APIs, on <c>SetGraphicsResourceSet</c>'s dynamic-offset overload and on
    /// its compute twin, and every shipped renderer already writes 256-aligned slots. One number governing all
    /// three rings is what lets one shared policy test assert it. And a device-derived stride makes the ring's
    /// arithmetic a function of the machine, which puts a device-shaped number under a golden-bearing path to
    /// save memory the fleet does not need.</para>
    ///
    /// <para><b>THE DEVICE'S OWN MINIMUM IS STILL READ, AND IT IS READ SOMEWHERE ELSE ON PURPOSE.</b>
    /// <see cref="MetalDeviceRequirements.UniformRingStride"/> is M-N4's probe asserting that the device's
    /// reported buffer-offset alignment DIVIDES this number, and it refuses the machine at creation when it does
    /// not. That check states the constant itself rather than importing it from here, because it is the reason a
    /// machine that could not honour the stride never reaches this type at all, and
    /// <c>MetalRingStrideTests</c> pins the two against each other so the pair cannot drift.</para>
    ///
    /// <para><b>WHAT SHRANK (M-M4).</b> Direct3D 11 owes a 16-constant round-up on both the first constant and
    /// the count, and its first version shipped the wrong one and silently dropped binds. Vulkan owes
    /// <c>rangeOffset + callerDynamicOffset + range &lt;= stride</c> against
    /// <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c>, because a descriptor carries a range.
    /// <c>setBufferOffset:</c> carries no length at all, so what is left here is the same statement with nothing
    /// to violate it except arithmetic: <see cref="BindWindowFits"/>. It is asserted device-free over every
    /// shipped set shape anyway, because Stride is section 9.4's backend-owned row and this backend owns
    /// its own.</para>
    ///
    /// <para>Everything here is pure arithmetic over plain integers, so the stride, the total allocation and the
    /// bind window are driven by ordinary <c>[Fact]</c>s on a machine with no Metal at all.</para>
    /// </summary>
    internal static class MetalRingStride
    {
        /// <summary>
        /// The alignment every segment stride is rounded to, 256 bytes. A FLAT floor rather than a maximum
        /// against a device limit, which is the M-M3 difference from the Vulkan sibling's
        /// <c>AlignmentFor</c>. See the class note.
        /// </summary>
        internal const uint SegmentAlignment = 256;

        /// <summary>
        /// The distance between two segments of a <paramref name="sizeInBytes"/> uniform buffer: the logical size
        /// rounded up to <see cref="SegmentAlignment"/>.
        /// </summary>
        /// <param name="sizeInBytes">The buffer's LOGICAL size, which is the only size the seam ever sees.</param>
        /// <exception cref="ArgumentOutOfRangeException">The size is zero, or rounding it up overflows.</exception>
        internal static uint SegmentStrideFor(uint sizeInBytes)
        {
            if (sizeInBytes == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "A zero-byte uniform buffer cannot be ring-backed. -newBufferWithLength:options: answers nil "
                    + "for a length of 0, and a segment of 0 has no base that means anything.");
            }

            ulong rounded = ((ulong)sizeInBytes + (SegmentAlignment - 1)) & ~((ulong)SegmentAlignment - 1);

            if (rounded > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "Rounding a native Metal uniform buffer of " + sizeInBytes + " bytes up to the "
                    + SegmentAlignment + "-byte ring stride overflows a 32-bit size. A buffer that large is not "
                    + "a uniform buffer.");
            }

            return (uint)rounded;
        }

        /// <summary>
        /// The WHOLE allocation a ring-backed buffer takes: <see cref="SegmentStrideFor"/> times the device's
        /// frame count. This is what <c>-newBufferWithLength:options:</c> is called with, and it is the one
        /// number the seam's caller does not know about their own buffer.
        /// <para>
        /// IT IS A <c>ulong</c> AND THE CALLER IS EXPECTED TO REFUSE A LARGE ONE. <c>-newBufferWithLength:</c>
        /// takes an <c>NSUInteger</c>, so nothing overflows on the way to the driver, but the rest of this
        /// backend carries buffer sizes as <c>uint</c> because the seam does. <c>MetalBuffer</c> is where that
        /// refusal lives, with the message that names the frames-in-flight knob as the lever.
        /// </para>
        /// </summary>
        /// <param name="sizeInBytes">The buffer's logical size.</param>
        /// <param name="framesInFlight">How many segments to cut it into, from
        /// <see cref="MetalFramesInFlight"/>.</param>
        internal static ulong TotalBytesFor(uint sizeInBytes, int framesInFlight)
        {
            if (framesInFlight < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    "A native Metal uniform ring needs at least one segment. "
                    + MetalFramesInFlight.EnvVarName + " clamps to "
                    + MetalFramesInFlight.Minimum + " and above before it gets here.");
            }

            return (ulong)SegmentStrideFor(sizeInBytes) * (ulong)framesInFlight;
        }

        /// <summary>
        /// THE BIND-WINDOW INVARIANT (M-M4): whether a bind of <paramref name="range"/> bytes at
        /// <paramref name="rangeOffset"/> with a caller dynamic offset of
        /// <paramref name="callerDynamicOffset"/> stays inside its own segment, which at the LAST frame slot is
        /// the same question as whether it stays inside the buffer.
        ///
        /// <para><b>NOTHING HERE ENFORCES IT AT RUNTIME AND THAT IS THE POINT.</b> Metal's
        /// <c>setBufferOffset:atIndex:</c> takes an offset and no length, so there is no descriptor range to
        /// overrun, no VUID to answer and no validation layer to trip. The invariant survives as arithmetic that
        /// row 13's composed offset has to satisfy, and asserting it device-free over every shipped set shape is
        /// the whole of what this backend owes section 9.4's Stride row. The failure it describes would be a
        /// shader reading the NEXT frame's uniforms on one frame slot in three, silently.</para>
        ///
        /// <para><b>AND THE SHAPE THAT LOOKS SAFE IS A RANGE EQUAL TO THE STRIDE</b>, which fits only while the
        /// caller's own offset is zero. It is non-zero in five shipped renderers.</para>
        /// </summary>
        /// <param name="rangeOffset">The set's own <c>GpuBufferRange.Offset</c>, 0 at every shipped site.</param>
        /// <param name="callerDynamicOffset">The largest per-draw dynamic offset the caller passes.</param>
        /// <param name="range">The window the bind reads: <c>GpuBufferRange.Size</c>, or the buffer's own logical
        /// size where the set was created from a bare buffer.</param>
        /// <param name="stride">The segment stride from <see cref="SegmentStrideFor"/>.</param>
        internal static bool BindWindowFits(uint rangeOffset, uint callerDynamicOffset, uint range, uint stride)
            => (ulong)rangeOffset + callerDynamicOffset + range <= stride;

        /// <summary>
        /// The same question as a refusal, for the bind path that must not compose an offset which reads past its
        /// own segment. Row 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/579) is the caller.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The window leaves its own segment.</exception>
        internal static void RequireBindWindowFits(uint rangeOffset, uint callerDynamicOffset, uint range,
            uint stride)
        {
            if (BindWindowFits(rangeOffset, callerDynamicOffset, range, stride)) return;

            throw new ArgumentOutOfRangeException(nameof(range), range,
                "A native Metal uniform bind of " + range + " bytes at range offset " + rangeOffset
                + " plus dynamic offset " + callerDynamicOffset + " runs past the end of its own " + stride
                + "-byte ring segment. Metal's setBufferOffset: carries no length, so nothing would report this: "
                + "the shader would read the NEXT frame's uniforms on the frame slots where there is a next one, "
                + "and the last frame slot would read past the buffer entirely. Widen the buffer or narrow the "
                + "window.");
        }
    }
}
