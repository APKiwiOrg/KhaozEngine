using System;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// THE DECAL PASS'S PER-PASS FRAME SLOTS: one 256-byte slot per pass a frame runs, one CPU mirror of the
    /// whole buffer, and the dynamic offset each pass binds. The fix for
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/483">#483</see>'s one confirmed hazard.
    /// <para>
    /// WHAT WAS WRONG. A frame runs this renderer TWICE and the two runs disagree about one lane. The blob-shadow
    /// pass draws early, before the skinned draws and before the normal target is resolved, and must NOT reject
    /// dynamic-tagged pixels. The main pass draws after that resolve and must. Both used to write the SAME 80
    /// bytes at offset 0 of one uniform buffer, with the blob pass's draws recorded between the two writes. That
    /// is exactly the shape the engine's three native backends collapse: a record-time <c>UpdateBuffer</c> to a
    /// uniform buffer there is a memcpy into the frame's own ring segment and is not ordered against the draws, so
    /// the LAST write in the frame decides every byte and both passes rendered with the main pass's reject on.
    /// <c>RecordTimeUniformRewriteGpuTests</c> is the measurement, taken on a real device rather than reasoned
    /// from the ring's doc comments.
    /// </para>
    /// <para>
    /// WHY SLOTS RATHER THAN A PER-DECAL LANE. Moving the reject flag into the per-instance attribute stream would
    /// also have removed the rewrite, and it would have changed a shader, an instance struct and the bytes every
    /// golden reads. Distinct slots plus a dynamic offset is the shape <c>OverlayMeshRenderer</c>,
    /// <c>WaterRenderer</c> and <c>SpriteBatch</c> already carry for the same reason, it is pixel-identical on
    /// every backend that was already ordered, and it moves nothing but which 96 bytes of a 512-byte buffer each
    /// pass reads.
    /// </para>
    /// <para>
    /// THE WHOLE-BUFFER WRITE STAYS WHOLE, and the rewrite it still performs is the one that is provably safe.
    /// Each pass packs its own slot and uploads the WHOLE mirror, so the other slot goes up again carrying the
    /// bytes it already held: a slot's mirror value does not change once its pass has packed it, so the second
    /// upload is a no-op for anything already recorded. That is the <c>SpriteBatch</c> argument, and it is what
    /// keeps the write off the blocking partial-uniform-write staging route the incumbent had on Direct3D 11 (#408) without
    /// reintroducing a collapse.
    /// </para>
    /// </summary>
    internal sealed partial class GroundDecalRenderer
    {
        /// <summary>Which of a frame's two decal passes is being recorded. The pass decides the UBO slot AND the
        /// dynamic-geometry reject, so the two cannot drift apart the way a separate bool could.</summary>
        internal enum FramePass
        {
            /// <summary>The early blob-shadow pass. Runs before the skinned draws and resolves only depth, so the
            /// normal target the reject reads is not yet valid (and a blob shadow wants no reject anyway).</summary>
            BlobShadow = 0,

            /// <summary>The main decal pass, after the depth+normal resolve, which rejects pixels the model pass
            /// tagged as dynamic so a ground decal never paints onto a character (issue #235).</summary>
            Main = 1,
        }

        /// <summary>The bytes one pass actually reads: <c>FrameUniforms</c>. This is the window the resource set
        /// binds, and the size <c>D3D11ResourceModelTests</c> checks the constant-count rule against.</summary>
        internal const uint FramePayloadBytes = 96;

        /// <summary>The distance between two passes' slots. 256 is the dynamic-offset alignment that is safe on
        /// Metal, Direct3D 11 and Vulkan alike, the same stride every other slotted UBO in the engine uses.</summary>
        internal const uint FrameSlotBytes = 256;

        /// <summary>How many slots the buffer holds: one per member of <see cref="FramePass"/>.</summary>
        internal const int FrameSlotCount = 2;

        /// <summary>The whole uniform buffer's size.</summary>
        internal const uint FrameUboBytes = FrameSlotBytes * FrameSlotCount;

        // The CPU mirror of the WHOLE buffer, so each pass's upload can cover it from offset 0. Carried across
        // frames deliberately: a pass repacks its own slot before every upload, so the carry-over only keeps the
        // OTHER slot and each slot's unread tail stable rather than being load-bearing.
        readonly byte[] _frameImage = new byte[FrameSlotCount * (int)FrameSlotBytes];

        /// <summary>The byte offset of a pass's slot, which is what the draw binds as its dynamic offset.</summary>
        internal static uint FrameSlotOffset(FramePass pass) => (uint)pass * FrameSlotBytes;

        /// <summary>Pack one pass's resolved frame block into its slot of the mirror. The GPU sees nothing until
        /// <see cref="UploadFrameSlots"/> runs, which <c>Draw</c> guarantees happens before its first draw.</summary>
        void PackFrameSlot(FramePass pass, in FrameUniforms frame) =>
            MemoryMarshal.Write(_frameImage.AsSpan((int)FrameSlotOffset(pass), (int)FrameSlotBytes), in frame);

        /// <summary>Upload the mirror WHOLE, ahead of the draws that bind a slot of it.</summary>
        void UploadFrameSlots(IGpuCommandList cl) =>
            cl.UpdateBuffer(_frameUbo, 0, (ReadOnlySpan<byte>)_frameImage);
    }
}
