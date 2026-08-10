using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// DECISION M-R6's ASSEMBLY: everything one flush writes into ONE stage's three argument tables, collected as
    /// (index, object, offset) triples and emitted as one array call per CONTIGUOUS RUN.
    ///
    /// <para><b>WHY A RUN AND NOT A SLOT.</b> <c>setVertexBuffers:offsets:withRange:</c> and its five siblings
    /// take an <c>NSRange</c>, so one call writes as many consecutive indices as the caller has objects for. A
    /// full activation of the engine's model set collapses to one buffer call, one texture call and one sampler
    /// call on the fragment stage plus one buffer call on the vertex stage. Emitting one call per resource per
    /// stage instead is the #418 fan-out defect arriving on a second API, which is a defect this program already
    /// paid to fix once, and it is why <see cref="IMetalEncoderSink"/> deliberately has no single-element
    /// overload for a per-slot entry point to be written against.</para>
    ///
    /// <para><b>A HOLE CUTS THE RUN AND IS NOT PADDED WITH NIL.</b> The obvious alternative is one call over the
    /// whole span with nil in the gaps, which is one native call instead of two. It would also UNBIND whatever is
    /// legitimately sitting in the gap: Metal's argument tables are absolute and per encoder, so an index this
    /// flush is not writing still holds what an earlier flush, or an earlier slot, put there. Two calls is the
    /// price of not clearing bindings nobody asked to clear.</para>
    ///
    /// <para><b>THE INDICES ARRIVE IN SLOT ORDER AND ARE EMITTED IN INDEX ORDER</b>, because the two are
    /// unrelated. An element's index is a fact about the emission (M-B1), so slot 0's uniform can land at buffer
    /// index 2 while slot 1's lands at 0, and over the shipped set 80 of 159 emitted arguments carry an index
    /// that differs from their binding number. The sort is an insertion sort because the entry count per flush is
    /// the number of bindings across the dirty slots, which is single digits at every shipped site.</para>
    ///
    /// <para><b>TWO ENTRIES AT ONE INDEX ARE REFUSED RATHER THAN RESOLVED.</b> It cannot happen through a
    /// correct index table (the emission gives each argument its own index within a space and a stage), so a
    /// collision means the table and the sets bound against it disagree about what is where, and the run it would
    /// produce is ambiguous in a way no later call reports. Naming it costs one comparison inside a sort that has
    /// already touched both entries.</para>
    ///
    /// <para><b>IT ALLOCATES ONCE PER LIST RATHER THAN ONCE PER DRAW.</b> The three entry arrays and the two
    /// emission scratch arrays grow to the high-water mark of one flush and are reused, so a frame of draws
    /// allocates nothing here. One of these belongs to each <see cref="MetalBindRecords"/> and each
    /// <see cref="MetalVertexStreamRecords"/>, which is per bind point per list, and nothing here is
    /// synchronised for the reason the list that owns it is not.</para>
    /// </summary>
    internal sealed class MetalArgumentBatch
    {
        // One list per argument table, because the three are independent index spaces and index 0 means three
        // different things (section 8.1). Indexed by (int)MetalIndexSpace.
        readonly Entry[][] _entries = [new Entry[8], new Entry[8], new Entry[8]];
        readonly int[] _counts = new int[3];

        IntPtr[] _objects = new IntPtr[8];
        nuint[] _offsets = new nuint[8];

        /// <summary>How many entries are staged for <paramref name="space"/>. For a test and for a
        /// diagnostic.</summary>
        internal int CountIn(MetalIndexSpace space) => _counts[(int)space];

        /// <summary>Drop everything staged, so the batch can be filled for the next stage. Called between
        /// stages and after every emission.</summary>
        internal void Clear() => Array.Clear(_counts);

        /// <summary>
        /// Stage one argument-table write.
        /// </summary>
        /// <param name="space">Which of the three tables, from the index table's own entry rather than from the
        /// element's declared kind.</param>
        /// <param name="index">The index within that table.</param>
        /// <param name="handle">The <c>MTLBuffer</c>, <c>MTLTexture</c> or <c>MTLSamplerState</c>, or
        /// <see cref="IntPtr.Zero"/> for a resource disposed since its set was created, which Metal reads as an
        /// unbound index rather than dereferencing a released pointer.</param>
        /// <param name="offset">The composed byte offset for a buffer, 0 for the other two spaces, which carry
        /// no window.</param>
        internal void Add(MetalIndexSpace space, int index, IntPtr handle, nuint offset)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "A native Metal argument-table write was staged at a negative index. An index comes from the "
                    + "binding table read out of the emission, which never produces one.");
            }

            int table = (int)space;
            Entry[] entries = _entries[table];
            if (_counts[table] == entries.Length)
            {
                Array.Resize(ref entries, entries.Length * 2);
                _entries[table] = entries;
            }

            entries[_counts[table]++] = new Entry(index, handle, offset);
        }

        /// <summary>
        /// EMIT EVERYTHING STAGED into <paramref name="encoder"/> for <paramref name="stage"/>, one array call
        /// per contiguous run per space, and clear.
        /// <para>
        /// GENERIC OVER THE SINK AND <c>ref</c> RATHER THAN <c>in</c>, so the JIT monomorphizes M-T2's seam away
        /// and no defensive copy is made per call on the per-draw path. That is the struct constraint arriving at
        /// one of its two real callers.
        /// </para>
        /// <para>
        /// THE CLEAR IS UNCONDITIONAL AND HAPPENS LAST. A throw out of a sink would otherwise leave entries
        /// staged for the next stage to emit a second time, which is a bind of one stage's resources into
        /// another's table.
        /// </para>
        /// </summary>
        internal void Emit<TSink>(ref TSink sink, MetalShaderStage stage, IntPtr encoder)
            where TSink : struct, IMetalEncoderSink
        {
            try
            {
                EmitSpace(ref sink, stage, encoder, MetalIndexSpace.Buffer);
                EmitSpace(ref sink, stage, encoder, MetalIndexSpace.Texture);
                EmitSpace(ref sink, stage, encoder, MetalIndexSpace.Sampler);
            }
            finally
            {
                Clear();
            }
        }

        void EmitSpace<TSink>(ref TSink sink, MetalShaderStage stage, IntPtr encoder, MetalIndexSpace space)
            where TSink : struct, IMetalEncoderSink
        {
            int table = (int)space;
            int count = _counts[table];
            if (count == 0) return;

            Entry[] entries = _entries[table];
            SortAndRefuseDuplicates(entries, count, space, stage);
            EnsureScratch(count);

            int start = 0;
            while (start < count)
            {
                int length = 1;
                while (start + length < count && entries[start + length].Index == entries[start + length - 1].Index + 1)
                    length++;

                for (int i = 0; i < length; i++)
                {
                    _objects[i] = entries[start + i].Handle;
                    _offsets[i] = entries[start + i].Offset;
                }

                var objects = new ReadOnlySpan<IntPtr>(_objects, 0, length);
                uint first = (uint)entries[start].Index;

                switch (space)
                {
                    case MetalIndexSpace.Buffer:
                        sink.SetBuffers(stage, encoder, objects, new ReadOnlySpan<nuint>(_offsets, 0, length),
                            first);
                        break;
                    case MetalIndexSpace.Texture:
                        sink.SetTextures(stage, encoder, objects, first);
                        break;
                    default:
                        sink.SetSamplerStates(stage, encoder, objects, first);
                        break;
                }

                start += length;
            }
        }

        // AN INSERTION SORT, and the duplicate check rides it rather than costing a second pass. The count here
        // is the number of bindings across the dirty slots that this stage actually references, which is single
        // digits everywhere the engine ships, and an insertion sort over that allocates nothing and beats a
        // comparison-delegate sort by more than the asymptotics suggest.
        static void SortAndRefuseDuplicates(Entry[] entries, int count, MetalIndexSpace space,
            MetalShaderStage stage)
        {
            for (int i = 1; i < count; i++)
            {
                Entry moving = entries[i];
                int j = i - 1;
                while (j >= 0 && entries[j].Index > moving.Index)
                {
                    entries[j + 1] = entries[j];
                    j--;
                }

                if (j >= 0 && entries[j].Index == moving.Index)
                {
                    throw new InvalidOperationException(
                        "Two native Metal bindings in one flush both landed at [["
                        + space.Word() + "(" + moving.Index.ToString(CultureInfo.InvariantCulture)
                        + ")]] on the " + stage.ToString().ToLowerInvariant() + " stage. The binding table gives "
                        + "each emitted argument its own index within a space and a stage, so this is the bound "
                        + "sets and the table disagreeing about what is where rather than a run this flush could "
                        + "resolve: one of the two resources would be written and then overwritten, silently, "
                        + "and the draw would read the wrong one.");
                }

                entries[j + 1] = moving;
            }
        }

        void EnsureScratch(int count)
        {
            if (_objects.Length >= count) return;

            int size = _objects.Length;
            while (size < count) size *= 2;

            _objects = new IntPtr[size];
            _offsets = new nuint[size];
        }

        readonly record struct Entry(int Index, IntPtr Handle, nuint Offset);
    }
}
