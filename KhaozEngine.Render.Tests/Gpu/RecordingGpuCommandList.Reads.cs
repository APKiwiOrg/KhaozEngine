using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE READ HALF OF THE RECORDING: which uniform bytes each recorded draw or dispatch actually bound.
    ///
    /// <para><b>WHY A WRITE TIMELINE ALONE IS NOT ENOUGH.</b> The ring collapse is a hazard only for bytes some
    /// draw between the two writes READS. The engine's sanctioned pattern is to pack one slot of a CPU mirror and
    /// upload the mirror WHOLE, so two passes' uploads legitimately differ in the slot the other pass owns. Judging
    /// that pair on the whole overlapping range calls shipped, correct code a collapse
    /// (<c>GroundDecalRenderer</c> and <c>SpriteBatch.ViewProj.cs</c> both have that shape). Judging it on the
    /// windows an intervening draw bound is the actual rule.</para>
    ///
    /// <para><b>WHAT IS RECORDED.</b> A bind is remembered per slot, graphics and compute kept apart because they
    /// are separate binding spaces, and every draw stamps the windows of every set bound at that moment with its
    /// own ordinal. A set stays bound until the same slot is bound again, which OVER-reports what a draw reads
    /// when a pass leaves a stale set behind at a slot its pipeline does not use. That direction is deliberate: it
    /// can only turn a safe rewrite into a reported hazard, never hide one.</para>
    ///
    /// <para><b>THE RESOLVER IS INJECTED, AND WITHOUT IT NOTHING IS RECORDED.</b> A command list cannot read a
    /// resource set's contents back, so <see cref="UniformWindowsOfSet"/> has to be handed in (from
    /// <c>UniformBufferTrackingGpuDevice.WindowsOf</c>). A caller that forgets gets an empty
    /// <see cref="Reads"/> list, which would make every rewrite look safe, so
    /// <see cref="UniformRewriteAudit.Scan"/>'s callers assert the list is non-empty before believing an empty
    /// answer.</para>
    /// </summary>
    internal sealed partial class RecordingGpuCommandList
    {
        /// <summary>One uniform window one recorded draw or dispatch bound, already rebased by that draw's dynamic
        /// offset. <see cref="DrawOrdinal"/> counts from zero and is directly comparable with
        /// <see cref="Upload.DrawsBefore"/>: a draw sits BETWEEN two uploads when its ordinal is at least the
        /// first upload's <c>DrawsBefore</c> and below the second's.</summary>
        internal readonly record struct BoundRead(IGpuBuffer Buffer, uint Offset, uint Bytes, int DrawOrdinal);

        readonly List<BoundRead> _reads = new();
        readonly Dictionary<uint, Binding> _graphicsSets = new();
        readonly Dictionary<uint, Binding> _computeSets = new();

        readonly record struct Binding(IGpuResourceSet Set, uint DynamicOffset);

        /// <summary>How to resolve a resource set into the uniform windows it binds. Set this from
        /// <c>UniformBufferTrackingGpuDevice.WindowsOf</c> before recording. Null (the default) records no reads at
        /// all, which every other GPU test here wants: only the rewrite audit reads them.</summary>
        public Func<IGpuResourceSet, IReadOnlyList<UniformWindow>>? UniformWindowsOfSet { get; set; }

        /// <summary>Every uniform window a draw or dispatch bound since the last <see cref="Clear"/>, in record
        /// order. Empty when <see cref="UniformWindowsOfSet"/> was never set.</summary>
        public IReadOnlyList<BoundRead> Reads => _reads;

        void NoteGraphicsSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _graphicsSets[slot] = new Binding(set, dynamicOffset);

        void NoteComputeSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => _computeSets[slot] = new Binding(set, dynamicOffset);

        void ClearReads()
        {
            _reads.Clear();
            _graphicsSets.Clear();
            _computeSets.Clear();
        }

        // Called as a draw or dispatch is recorded, BEFORE _draws is incremented, so the read carries the ordinal
        // of the draw that performs it.
        void NoteReads(Dictionary<uint, Binding> bound)
        {
            Func<IGpuResourceSet, IReadOnlyList<UniformWindow>>? resolve = UniformWindowsOfSet;
            if (resolve is null || bound.Count == 0) return;

            foreach (KeyValuePair<uint, Binding> slot in bound)
            {
                IReadOnlyList<UniformWindow> windows = resolve(slot.Value.Set);
                for (int i = 0; i < windows.Count; i++)
                {
                    UniformWindow w = windows[i].Rebased(slot.Value.DynamicOffset);
                    _reads.Add(new BoundRead(w.Buffer, w.Offset, w.Bytes, _draws));
                }
            }
        }

        void NoteGraphicsReads() => NoteReads(_graphicsSets);
        void NoteComputeReads() => NoteReads(_computeSets);
    }
}
