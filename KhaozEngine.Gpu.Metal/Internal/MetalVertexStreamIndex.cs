using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-B2's NUMBERING, IN ONE PLACE, because it is the one number in this backend that TWO
    /// independent pieces of code have to agree on and neither can check the other (section 8.3).
    ///
    /// <para><b>THE COLLISION THIS ANSWERS.</b> Vertex STREAM buffers and resource buffers share the
    /// <c>[[buffer(n)]]</c> space of the vertex stage. The fork's <c>ResourceBindingModel</c> makes one numbering
    /// depend on the other's COUNT in either direction, and reproducing its <c>Improved</c> arm would be unsound
    /// under M-B1: <c>NonVertexBufferCount + i</c> assumes the resource buffers occupy
    /// <c>0..NonVertexBufferCount-1</c>, which is exactly the CPU-side count the index table removes as the
    /// authority. So streams are pinned at the TOP instead and resource buffers grow from 0 upward wherever the
    /// emission put them, and neither depends on the other's count.</para>
    ///
    /// <para><b>TWO CALLERS, AND GETTING THEM OUT OF STEP BINDS A VERTEX BUFFER WHERE A UNIFORM SHOULD BE.</b> The
    /// <c>MTLVertexDescriptor</c>'s layout index is row 11's (https://github.com/APKiwiOrg/KhaozEngine/issues/577)
    /// and the <c>setVertexBuffers:</c> index is the bind flush's
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/579). Both are this backend's own, nothing outside it can
    /// see either, and a device reports NOTHING when they disagree: the vertex function reads its attributes
    /// through <c>[[stage_in]]</c>, so a stream bound at the wrong index simply feeds the shader whatever else is
    /// there. That is why the mapping is a type rather than a subtraction written twice.</para>
    ///
    /// <para><b>AND THIS CHANGES NO PIXEL, WHICH IS WHAT MAKES IT FREE.</b> A stream's buffer index is invisible
    /// to the emitted MSL. It only has to agree between those two sites.</para>
    ///
    /// <para><b>THE NO-COLLISION PROPERTY IS ROW 11's ASSERTION AND NOT THIS TYPE'S.</b> The two numberings can
    /// only meet if one pipeline declares more than <see cref="BufferTableSize"/> combined bindings on one stage,
    /// and the check for that compares indices READ OUT OF the vertex function against
    /// <see cref="LowestIndexFor"/>, which needs a pipeline's stream count and its index table together. This type
    /// hands that check its number and makes no claim of its own.</para>
    /// </summary>
    internal static class MetalVertexStreamIndex
    {
        /// <summary>
        /// How many entries one stage's buffer argument table has, which is Metal's own limit across every
        /// family this engine targets. Both numberings live inside it, from opposite ends.
        /// </summary>
        internal const int BufferTableSize = 31;

        /// <summary>
        /// The index stream 0 takes, which is the TOP of the buffer table. Streams count DOWNWARD from here, so
        /// stream 0 is 30, stream 1 is 29, and so on.
        /// </summary>
        internal const int TopIndex = BufferTableSize - 1;

        /// <summary>
        /// The <c>[[buffer(n)]]</c> index of vertex stream <paramref name="slot"/>.
        /// </summary>
        /// <param name="slot">The seam's vertex-buffer slot, as <c>IGpuCommandList.SetVertexBuffer</c> names
        /// it.</param>
        /// <exception cref="ArgumentOutOfRangeException">The slot is past the bottom of the buffer table, which is
        /// a pipeline declaring more vertex streams than one Metal stage can hold at all.</exception>
        internal static uint ForSlot(uint slot)
        {
            if (slot < BufferTableSize) return (uint)TopIndex - slot;

            throw new ArgumentOutOfRangeException(nameof(slot), slot,
                "A native Metal vertex stream was asked for at slot "
                + slot.ToString(CultureInfo.InvariantCulture) + ", and one stage's buffer argument table holds "
                + BufferTableSize.ToString(CultureInfo.InvariantCulture)
                + " entries in total. Streams are pinned at the TOP of that table and count downward (M-B2), so "
                + "this slot has no index at either end rather than colliding with a resource buffer.");
        }

        /// <summary>
        /// The LOWEST index a pipeline declaring <paramref name="streamCount"/> streams occupies, which is the
        /// floor a resource buffer's index must stay below. <see cref="BufferTableSize"/> for a pipeline with no
        /// streams at all, which is correct rather than a special case: it claims nothing, so nothing is off
        /// limits.
        /// <para>
        /// ROW 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/577) IS THE CALLER, asserting that no index the
        /// index table read out of that program's vertex function reaches this number. Written here so the floor
        /// and the numbering that produces it cannot drift apart.
        /// </para>
        /// </summary>
        /// <param name="streamCount">How many vertex streams the pipeline's vertex descriptor declares.</param>
        internal static int LowestIndexFor(int streamCount)
        {
            if (streamCount >= 0 && streamCount <= BufferTableSize) return BufferTableSize - streamCount;

            throw new ArgumentOutOfRangeException(nameof(streamCount), streamCount,
                "A native Metal pipeline declares " + streamCount.ToString(CultureInfo.InvariantCulture)
                + " vertex streams and one stage's buffer argument table holds "
                + BufferTableSize.ToString(CultureInfo.InvariantCulture) + " entries.");
        }
    }
}
