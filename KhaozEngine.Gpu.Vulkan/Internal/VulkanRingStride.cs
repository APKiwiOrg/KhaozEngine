using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE RING'S ARITHMETIC AND THE ONE INVARIANT IT OWES A VUID. Decisions V-M5 and V-M6, section 9.2.
    ///
    /// <para><b>THE STRIDE IS THE SPACING OF THE SEGMENTS AND IS NEVER THE DESCRIPTOR'S RANGE.</b> A ring-backed
    /// uniform buffer is ONE <c>VkBuffer</c> of <c>stride * FramesInFlight</c> bytes, where
    /// <c>stride = align(size, max(256, minUniformBufferOffsetAlignment))</c>. The descriptor written at set
    /// creation (row 10) takes the BIND WINDOW as its range instead, and the two numbers are different on purpose:
    /// see <see cref="BindWindowFits"/>.</para>
    ///
    /// <para><b>THE 256 IS DERIVED HERE RATHER THAN BORROWED FROM THE OTHER BACKEND (2.2).</b> Direct3D 11 rounds
    /// to 256 because <c>*SetConstantBuffers1</c> counts in 16-byte constants and wants the first constant on a
    /// 16-constant boundary. That argument does not exist on this API. The reason to floor the stride at 256 here
    /// is that 256 is the Vulkan spec's REQUIRED MAXIMUM for <c>minUniformBufferOffsetAlignment</c>, so a stride
    /// floored there is a legal dynamic offset on every conformant device without the arithmetic depending on what
    /// this particular driver reported. A constant with two independent derivations is not shared code.</para>
    ///
    /// <para><b>WHICH IS ALSO WHY A MISSING LIMIT READ IS SAFE.</b> Because 256 is the required maximum, the
    /// device's own value can only ever LOWER the alignment, never raise it, so <see cref="AlignmentFor"/> answers
    /// 256 on every conformant device and the read is belt and braces rather than the load-bearing term. A
    /// <see cref="VulkanMemoryFacts"/> built without one degrades to exactly the floor, which is always legal.</para>
    ///
    /// <para>Everything here is pure arithmetic over plain integers, so the stride, the total allocation and the
    /// bind-window invariant are driven by ordinary <c>[Fact]</c>s on a machine with no Vulkan loader.</para>
    /// </summary>
    internal static class VulkanRingStride
    {
        /// <summary>
        /// The floor every segment stride is aligned to, 256 bytes: the Vulkan spec's required MAXIMUM for
        /// <c>VkPhysicalDeviceLimits.minUniformBufferOffsetAlignment</c>. Flooring here makes the stride
        /// device-independent, which is what keeps a golden-bearing path off a device-shaped number.
        /// </summary>
        internal const ulong OffsetAlignmentFloor = 256;

        /// <summary>
        /// The alignment a segment stride is rounded to on a device reporting
        /// <paramref name="minUniformBufferOffsetAlignment"/>: that value or
        /// <see cref="OffsetAlignmentFloor"/>, whichever is larger.
        /// </summary>
        /// <param name="minUniformBufferOffsetAlignment">The device limit. A non-zero power of two by spec, and 0
        /// is treated as "not read" and answers the floor.</param>
        internal static ulong AlignmentFor(ulong minUniformBufferOffsetAlignment)
        {
            if (minUniformBufferOffsetAlignment == 0) return OffsetAlignmentFloor;

            if (!VulkanMemoryFreeList.IsPowerOfTwo(minUniformBufferOffsetAlignment))
            {
                throw new ArgumentOutOfRangeException(nameof(minUniformBufferOffsetAlignment),
                    minUniformBufferOffsetAlignment,
                    "minUniformBufferOffsetAlignment must be a non-zero power of two, which the Vulkan spec "
                    + "requires of it. A value that is not means the limit was not read off a device.");
            }

            return Math.Max(OffsetAlignmentFloor, minUniformBufferOffsetAlignment);
        }

        /// <summary>
        /// The distance between two segments of a <paramref name="sizeInBytes"/> uniform buffer: the logical size
        /// rounded up to <see cref="AlignmentFor"/>.
        /// </summary>
        /// <param name="sizeInBytes">The buffer's LOGICAL size, which is the only size the seam ever sees.</param>
        /// <param name="minUniformBufferOffsetAlignment">The device limit, or 0 for the floor.</param>
        internal static ulong SegmentStrideFor(ulong sizeInBytes, ulong minUniformBufferOffsetAlignment)
        {
            if (sizeInBytes == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "A zero-byte uniform buffer cannot be ring-backed. vkCreateBuffer rejects a size of 0, and a "
                    + "segment of 0 has no base that means anything.");
            }

            ulong alignment = AlignmentFor(minUniformBufferOffsetAlignment);

            if (!VulkanMemoryFreeList.TryAlignUp(sizeInBytes, alignment, out ulong stride))
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "Rounding a native Vulkan uniform buffer of "
                    + sizeInBytes.ToString(CultureInfo.InvariantCulture)
                    + " bytes up to the "
                    + alignment.ToString(CultureInfo.InvariantCulture)
                    + "-byte dynamic-offset alignment overflows. A buffer that large is not a uniform buffer.");
            }

            return stride;
        }

        /// <summary>
        /// The WHOLE allocation a ring-backed buffer takes: <see cref="SegmentStrideFor"/> times the device's
        /// frame count. This is what the native <c>VkBuffer</c> is created with, and it is the one number the
        /// seam's caller does not know about their own buffer.
        /// </summary>
        internal static ulong TotalBytesFor(ulong sizeInBytes, int framesInFlight,
            ulong minUniformBufferOffsetAlignment)
        {
            if (framesInFlight < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    "A native Vulkan uniform ring needs at least one segment.");
            }

            ulong stride = SegmentStrideFor(sizeInBytes, minUniformBufferOffsetAlignment);

            if (stride > ulong.MaxValue / (ulong)framesInFlight)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "A uniform buffer of "
                    + sizeInBytes.ToString(CultureInfo.InvariantCulture)
                    + " bytes across "
                    + framesInFlight.ToString(CultureInfo.InvariantCulture)
                    + " frame segments overflows a 64-bit size. Lower "
                    + VulkanFramesInFlight.EnvVarName
                    + " or split the buffer.");
            }

            return stride * (ulong)framesInFlight;
        }

        /// <summary>
        /// THE BIND-WINDOW INVARIANT (V-M6), and the reason the descriptor's range is NOT the stride.
        ///
        /// <para><b>WHAT THE VUID ASKS.</b> <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c> requires the
        /// EFFECTIVE offset plus the descriptor's range to stay inside the buffer, and the effective offset on a
        /// ring-backed uniform bind is <c>frameBase + rangeOffset + callerDynamicOffset</c>. At the last frame slot
        /// <c>frameBase</c> is <c>(FramesInFlight - 1) * stride</c>, so a range equal to the STRIDE overruns the
        /// buffer by exactly the caller's own offset the moment that offset is non-zero.</para>
        ///
        /// <para><b>AND IT IS NON-ZERO IN FIVE SHIPPED RENDERERS</b>, which is what turns a theoretical VUID into a
        /// validation error on the last frame slot only: <c>ShadowMapRenderer</c> passes <c>cascade *
        /// CascadeSlotBytes</c> and <c>slot * SkinnedDepthSlotBytes</c>, <c>ModelRenderer</c> <c>slot *
        /// SkinnedMainSlotBytes</c>, <c>WaterRenderer</c> and <c>OverlayMeshRenderer</c> a per-plane and a per-draw
        /// slot, and <c>SpriteBatch</c> its view-projection slot. Every one of those sets is created from a
        /// <c>GpuBufferRange(buffer, 0, slotBytes)</c>, so the window is already on the seam and the descriptor
        /// takes it verbatim.</para>
        ///
        /// <para><b>THIS IS THE SAME INVARIANT AN UNRINGED BUFFER ALREADY OBEYS.</b> A windowed bind with a dynamic
        /// offset must stay inside the buffer on any backend. The ring adds <c>frameBase</c> to the offset and
        /// <c>stride</c> to the ceiling and leaves the arithmetic otherwise untouched, so satisfying it here
        /// satisfies the VUID at EVERY frame slot at once rather than at the one a test happened to reach.</para>
        /// </summary>
        /// <param name="rangeOffset">The set's own <c>GpuBufferRange.Offset</c>, 0 at every shipped site.</param>
        /// <param name="callerDynamicOffset">The largest per-draw dynamic offset the caller passes.</param>
        /// <param name="range">The descriptor's range: <c>GpuBufferRange.Size</c>, or the buffer's own logical size
        /// where the set was created from a bare buffer. Never <c>VK_WHOLE_SIZE</c> and never the stride.</param>
        /// <param name="stride">The segment stride from <see cref="SegmentStrideFor"/>.</param>
        internal static bool BindWindowFits(ulong rangeOffset, ulong callerDynamicOffset, ulong range, ulong stride)
        {
            ulong window = rangeOffset + callerDynamicOffset;

            // Overflow is a fit failure rather than a wrap, because a wrapped sum would answer "fits" for a window
            // that starts past the end of a 64-bit address space.
            if (window < rangeOffset) return false;

            ulong end = window + range;
            if (end < window) return false;

            return end <= stride;
        }

        /// <summary>
        /// The same question as a refusal, for the creation and bind paths that must not build a descriptor the
        /// validation layer would reject on the last frame slot alone.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The window leaves its own segment.</exception>
        internal static void RequireBindWindowFits(ulong rangeOffset, ulong callerDynamicOffset, ulong range,
            ulong stride)
        {
            if (BindWindowFits(rangeOffset, callerDynamicOffset, range, stride)) return;

            throw new ArgumentOutOfRangeException(nameof(range), range,
                "A native Vulkan uniform bind of "
                + range.ToString(CultureInfo.InvariantCulture)
                + " bytes at range offset "
                + rangeOffset.ToString(CultureInfo.InvariantCulture)
                + " plus dynamic offset "
                + callerDynamicOffset.ToString(CultureInfo.InvariantCulture)
                + " runs past the end of its own "
                + stride.ToString(CultureInfo.InvariantCulture)
                + "-byte ring segment. On the LAST frame slot that is also past the end of the buffer, which is "
                + "VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979, so it would be a validation error on one "
                + "frame in three rather than on every frame. Widen the buffer or narrow the window.");
        }
    }
}
