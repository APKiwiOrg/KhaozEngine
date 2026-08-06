using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE POSITIONAL <c>pDynamicOffsets</c> ARRAY, COMPOSED FRESH FOR EVERY BIND. Section 6.2's "the dynamic
    /// offset array is where a subtle mistake lives", as a type whose only job is to get it right.
    ///
    /// <para><b>IT IS POSITIONAL AND IT COVERS SETS THE CALLER NEVER NAMED.</b> One entry for every dynamic
    /// descriptor in every set of the run being bound, in SET ORDER then BINDING ORDER, including ring bases for
    /// uniform buffers nobody asked about. Bind a run of three sets and the array carries an entry for each dynamic
    /// descriptor in all three, whether or not the caller passed an offset for any of them. There is no key and no
    /// name anywhere in it: position is the only thing that says which entry belongs to which descriptor, which is
    /// why an off-by-one here reads the wrong slice of the RIGHT buffer and renders plausible garbage rather than
    /// throwing.</para>
    ///
    /// <para><b>EACH ENTRY IS <c>ringBase(buffer, currentFrame) + rangeOffset + (declaredDynamic ?
    /// engineOffset : 0)</c>.</b> The ring base moves every frame, the range offset is fixed for the set's life
    /// (row 10 resolved it at creation), and the caller's own per-draw offset is added for the ONE element the
    /// engine declared dynamic and for no other. That last term is the only thing
    /// <see cref="GpuResourceLayoutElement.Dynamic"/> decides on this backend (V-D4): the descriptor TYPE is
    /// <c>UNIFORM_BUFFER_DYNAMIC</c> for every uniform buffer regardless, because the per-frame ring base has to be
    /// applied at bind and the dynamic offset is the only bind-time knob Vulkan offers on one.</para>
    ///
    /// <para><b>WHICH IS ALSO WHY THERE IS NO "OFFSETS ONLY" STATE TO SKIP THIS WORK (V-R5).</b> Every ring-backed
    /// uniform's base moves every frame, so the array is recomposed on ANY bind whatever changed. A third dirty
    /// state would change no call and skip no work.</para>
    ///
    /// <para><b>THE COUNT IS RESET AT EVERY RUN AND THAT IS THE INCUMBENT'S OWN BUG, NOT INHERITED.</b> Its
    /// batching flush resets the batch count and the first set but NOT the accumulated dynamic-offset count, so a
    /// second batch inside one flush passes a too-large count built from stale entries. Here
    /// <see cref="Reset"/> is called by the flush at the head of every run and the count that reaches
    /// <c>vkCmdBindDescriptorSets</c> is by construction the sum of that run's sets' dynamic descriptors. The
    /// budget test asserts exactly that equality, which is what pins it against an edit rather than against a
    /// reading.</para>
    ///
    /// <para><b>AND EVERY COMPOSED ENTRY IS CHECKED AGAINST THE DESCRIPTOR'S OWN RANGE (V-M6).</b>
    /// <c>VUID-vkCmdBindDescriptorSets-pDescriptorSets-01979</c> measures the offset composed HERE against the
    /// range row 10 wrote and the stride row 8 owns, and the three have to agree or validation fails on the LAST
    /// frame slot only, which is the hardest version of this bug to find. Row 10 states the invariant at creation
    /// with a caller offset of zero, because zero is all it can know. This is where a real caller offset arrives,
    /// and five shipped renderers pass a non-zero one, so this is where it can really fail. The check is a handful
    /// of integer operations per dynamic descriptor and is deliberately NOT behind the validation gate: it costs
    /// nothing measurable and the thing it prevents is invisible without it.</para>
    ///
    /// <para><b>ONE INSTANCE PER BIND RECORD, GROWN ONCE AND REUSED FOREVER.</b> The hot path is thousands of
    /// offsets-only rebinds of one set per frame, so an array allocated per bind would be an allocation per rebind.
    /// Nothing here is synchronised, on the same grounds as the record that owns it: one list records on one thread
    /// at a time.</para>
    /// </summary>
    internal sealed class VulkanDynamicOffsets
    {
        uint[] _offsets = new uint[8];
        int _count;

        /// <summary>The entries composed so far, in the order <c>pDynamicOffsets</c> is positional in.</summary>
        internal ReadOnlySpan<uint> Composed => _offsets.AsSpan(0, _count);

        /// <summary>How many entries this bind will carry. Equal to the sum of the run's sets' dynamic descriptor
        /// counts, and that equality is the invariant the budget test pins.</summary>
        internal int Count => _count;

        /// <summary>Start a fresh array. Called at the head of EVERY run rather than once per flush: see the class
        /// note for the incumbent bug that distinction is.</summary>
        internal void Reset() => _count = 0;

        /// <summary>
        /// Append one set's dynamic descriptors, in binding order, to the run being composed.
        /// </summary>
        /// <param name="set">The bound set's plain-data view, whose <see cref="VulkanBoundSet.DynamicUniforms"/>
        /// row 10 resolved at creation and left in binding order.</param>
        /// <param name="callerDynamicOffset">The per-draw offset the caller recorded against this slot, added to
        /// the ONE element declared dynamic and to no other.</param>
        /// <param name="slot">The set number, for the refusal message only.</param>
        /// <exception cref="ArgumentOutOfRangeException">A composed entry would put the descriptor's range outside
        /// its own ring segment, which at the last frame slot is outside the buffer (V-M6), or would not fit the
        /// 32-bit field <c>pDynamicOffsets</c> is.</exception>
        internal void Append(in VulkanBoundSet set, uint callerDynamicOffset, uint slot)
        {
            VulkanDynamicUniform[]? dynamics = set.DynamicUniforms;
            if (dynamics is null || dynamics.Length == 0) return;

            EnsureCapacity(_count + dynamics.Length);

            for (int i = 0; i < dynamics.Length; i++)
            {
                _offsets[_count++] = Compose(in dynamics[i], callerDynamicOffset, slot);
            }
        }

        /// <summary>
        /// ONE ENTRY, and the whole of the composition rule. Exposed so the device-free test that pins it for every
        /// shipped layout shape asserts against the SHIPPED arithmetic rather than against a second copy of it.
        /// </summary>
        internal static uint Compose(in VulkanDynamicUniform dynamicUniform, uint callerDynamicOffset, uint slot)
        {
            // THE DECLARED FLAG DECIDES EXACTLY THIS AND NOTHING ELSE (V-D4). Every other dynamic descriptor in the
            // run still gets an entry, carrying its ring base and its range offset, because the array is positional
            // and skipping one would shift every entry after it onto the wrong descriptor.
            ulong applied = dynamicUniform.AppliesCallerOffset ? callerDynamicOffset : 0UL;

            // V-M6, with a REAL caller offset rather than the zero row 10 had to assume. See the class note.
            VulkanRingStride.RequireBindWindowFits(
                dynamicUniform.RangeOffset, applied, dynamicUniform.Range,
                dynamicUniform.Ring.SegmentStrideBytes);

            ulong composed = dynamicUniform.Ring.CurrentFrameBaseBytes + dynamicUniform.RangeOffset + applied;

            if (composed > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(dynamicUniform), composed,
                    "A native Vulkan dynamic offset of "
                    + composed.ToString(CultureInfo.InvariantCulture)
                    + " bytes at binding "
                    + dynamicUniform.Binding.ToString(CultureInfo.InvariantCulture)
                    + " of set "
                    + slot.ToString(CultureInfo.InvariantCulture)
                    + " does not fit the 32-bit field pDynamicOffsets is. A ring-backed uniform buffer whose whole "
                    + "allocation reaches 4 GiB is not a uniform buffer, so this is a size mistake upstream rather "
                    + "than something a bind can round.");
            }

            return (uint)composed;
        }

        // Doubling from eight, so a run over the engine's widest shipped shapes never reallocates after the first
        // frame and a wider one pays a handful of copies once. Array.Resize preserves what is already composed,
        // which matters because this is reached MID-RUN.
        void EnsureCapacity(int required)
        {
            if (required <= _offsets.Length) return;

            int capacity = _offsets.Length;
            while (capacity < required) capacity <<= 1;
            Array.Resize(ref _offsets, capacity);
        }
    }
}
