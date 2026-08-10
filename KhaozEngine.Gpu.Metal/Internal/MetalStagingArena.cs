using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE PER-LIST STAGING ARENA (M-M8, section 9.3): where a record-time <c>UpdateBuffer</c> to a NON-uniform
    /// buffer puts its bytes on the way to a
    /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c>.
    ///
    /// <para><b>THIS IS NOT THE RING AND MUST NOT BECOME IT.</b> Bulk payloads are rare relative to the uniform
    /// sites and they are exactly the traffic the ring is not for: they DO open a blit encoder, and on this API
    /// that costs the next draw a full graphics-state re-activation, because ending a render encoder discards the
    /// pipeline, every argument-table entry, the viewport, the scissor and every vertex stream (M-R4). Moving the
    /// uniform writes off this path is the whole win of row 8, and this type is the consolation prize for the
    /// traffic that genuinely cannot be moved.</para>
    ///
    /// <para><b>WHAT IT REMOVES IS AN ALLOCATION STORM.</b> The incumbent's <c>MTLCommandList</c> allocates a
    /// whole <c>MTLBuffer</c> per record-time upload and releases it immediately, and its own source carries a
    /// TODO asking for the buffers to be pooled. A scene load or a per-frame vertex stream is thousands of those.
    /// The cap here is <see cref="DefaultRetentionBytes"/> of retained free blocks, which is a policy number
    /// rather than a measured one.</para>
    ///
    /// <para><b>POOLED BY SIZE, WHICH IS WHY THE BLOCK SIZE IS ROUNDED TO A POWER OF TWO.</b> A pool keyed on the
    /// exact byte count would hold one block per distinct upload size and reuse almost nothing, which is a
    /// different way to reach the same storm. Rounding up to a power of two, floored at
    /// <see cref="DefaultBlockBytes"/>, gives a small number of classes that a load actually revisits, and the
    /// slack inside a block is reclaimed by SUB-ALLOCATION rather than wasted: several small uploads share one
    /// block by bumping through it.</para>
    ///
    /// <para><b>RECYCLED WHEN THE LIST'S OWN TIMELINE VALUE IS REACHED, WHICH IS WHERE THIS DIVERGES FROM THE
    /// VULKAN SIBLING AND WHY.</b> A block's bytes are read by the GPU when the copy executes, so returning it at
    /// the copy would hand it back while it is still being read. Over there the safe boundary is free: the list
    /// owns a <c>VkCommandPool</c> ring, and its <c>Begin</c> already waits for the slot it advances onto, so the
    /// blocks that slot filled last time round are provably finished with. There is no such pool here (M-R2), so
    /// this arena carries its own proof instead: each slot remembers the timeline value the list's submission
    /// took while that slot was open, and <see cref="BeginSlot"/> hands a slot's blocks back only once the
    /// device's completion counter has passed it. Per LIST rather than per device, so two lists recording
    /// concurrently (M-R3) never gate on each other, and a list that recorded uploads and was never submitted
    /// keeps its blocks rather than recycling memory nothing proved finished.</para>
    ///
    /// <para><b>AND IT NEVER BLOCKS.</b> A slot whose value has not been reached is simply not recycled: the
    /// arena opens fresh blocks for it and gives the old ones back at a later visit. The ONE wait a
    /// <c>Begin</c> may do is the ring's segment gate, which is MM4's single backpressure source, and an arena
    /// that could also wait would put a second meaning into that number.</para>
    ///
    /// <para><b>NOTHING HERE IS THREAD-SAFE, AND THAT IS THE POINT.</b> One arena belongs to one list, and a list
    /// is one thread's at a time. Two lists recording on two threads never touch the same arena, so the record
    /// path takes no lock, exactly as the encoder scope does not.</para>
    ///
    /// <para><b>EVERYTHING ABOVE THE TWO NATIVE CALLS IS DEVICE-FREE</b>, behind
    /// <see cref="IMetalStagingSource"/>, so the size classes, the sub-allocation, the recycling boundary and the
    /// retention cap all run under <c>dotnet test</c> on a machine with no Metal.</para>
    /// </summary>
    internal sealed class MetalStagingArena : IDisposable
    {
        /// <summary>
        /// The smallest block the arena ever creates, 64 KiB.
        /// <para>
        /// Big enough that a run of small uploads shares one block rather than taking one each, and small enough
        /// that an arena holding a handful of them costs nothing measurable. It is a policy number and moving it
        /// is a deliberate edit rather than a drift.
        /// </para>
        /// </summary>
        internal const ulong DefaultBlockBytes = 64UL * 1024;

        /// <summary>
        /// How many bytes of FREE blocks one arena keeps between recyclings, 8 MiB. M-M8's "real retention cap",
        /// against an incumbent that retains nothing at all.
        /// <para>
        /// It bounds the KEPT blocks rather than the live ones: a single upload larger than this still gets its
        /// own block, it simply is not retained afterwards. Retaining nothing is the incumbent's behaviour and
        /// retaining everything would let a one-off vertex stream pin its peak for the process's life, so the cap
        /// is what makes "pooled" mean something without making it unbounded.
        /// </para>
        /// </summary>
        internal const ulong DefaultRetentionBytes = 8UL * 1024 * 1024;

        /// <summary>
        /// The offset alignment every lease takes, four bytes.
        /// <para>
        /// <c>copyFromBuffer:sourceOffset:toBuffer:destinationOffset:size:</c> requires both offsets and the size
        /// to be multiples of four on macOS, which is the same constraint section 9.3's <c>CopyBuffer</c> ruling
        /// is about and the same four <see cref="MetalBufferPolicy.AllocationBytes"/> rounds every buffer up to.
        /// The arena controls the SOURCE offset, so it takes the alignment here and the caller never has to think
        /// about it.
        /// </para>
        /// </summary>
        internal const ulong CopyAlignment = 4;

        readonly IMetalStagingSource _source;
        readonly ulong _blockBytes;
        readonly ulong _retentionBytes;

        // Blocks being written into, PER SLOT, with how far each has been bumped. Newest last within a slot,
        // because a run of uploads keeps landing in the block the previous one opened.
        readonly List<OpenBlock>[] _open;

        // The timeline value the list's submission took while each slot was open, or 0 for a slot nothing has
        // been submitted from. The recycling gate's input, and the reason this arena needs no command-pool ring
        // to inherit a proof from.
        readonly ulong[] _slotValue;

        // Blocks returned by a recycle and not yet re-taken, smallest first so a request takes the smallest one
        // that fits rather than carving a large block for a tiny upload.
        readonly List<MetalStagingBlock> _free = new();

        int _slot;
        ulong _retained;
        bool _disposed;

        /// <param name="source">The two native calls.</param>
        /// <param name="framesInFlight">How many slots to keep, from <see cref="MetalFramesInFlight"/>. The same
        /// depth the ring is cut into, because the list's <c>Begin</c> rotates both.</param>
        /// <param name="blockBytes">The smallest block to create. Defaults to
        /// <see cref="DefaultBlockBytes"/>.</param>
        /// <param name="retentionBytes">How many bytes of free blocks to keep across a recycle. Defaults to
        /// <see cref="DefaultRetentionBytes"/>. Zero means keep nothing, which is the incumbent's shape and is
        /// constructible so a test can pin the difference.</param>
        internal MetalStagingArena(IMetalStagingSource source, int framesInFlight,
            ulong blockBytes = DefaultBlockBytes, ulong retentionBytes = DefaultRetentionBytes)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentOutOfRangeException.ThrowIfZero(blockBytes);

            if (framesInFlight < MetalFramesInFlight.Minimum || framesInFlight > MetalFramesInFlight.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    "A native Metal staging arena runs between " + MetalFramesInFlight.Minimum + " and "
                    + MetalFramesInFlight.Maximum + " slots, matching the uniform ring the list that owns it "
                    + "rotates with.");
            }

            _source = source;
            _blockBytes = blockBytes;
            _retentionBytes = retentionBytes;

            _open = new List<OpenBlock>[framesInFlight];
            for (int i = 0; i < framesInFlight; i++) _open[i] = new List<OpenBlock>();

            _slotValue = new ulong[framesInFlight];
        }

        /// <summary>How many slots this arena keeps open sets for.</summary>
        internal int Depth => _open.Length;

        /// <summary>The slot <see cref="Take"/> currently sub-allocates into.</summary>
        internal int Slot => _slot;

        /// <summary>How many blocks this arena has ever asked the source to create. The number the incumbent's
        /// allocate-per-upload shape makes equal to the upload count, and the one reading of the fix.</summary>
        internal int BlocksCreated { get; private set; }

        /// <summary>How many blocks the retention cap has turned away and released.</summary>
        internal int BlocksDestroyed { get; private set; }

        /// <summary>How many blocks are being written into right now, across every slot.</summary>
        internal int OpenBlockCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < _open.Length; i++) count += _open[i].Count;
                return count;
            }
        }

        /// <summary>How many blocks are pooled and idle.</summary>
        internal int FreeBlockCount => _free.Count;

        /// <summary>How many bytes of idle blocks are held, which is what <see cref="DefaultRetentionBytes"/>
        /// caps.</summary>
        internal ulong RetainedBytes => _retained;

        /// <summary>The timeline value one slot's blocks are waiting on, or 0 when nothing was submitted while it
        /// was open. The recycling gate's input, exposed for a test and a diagnostic.</summary>
        internal ulong SlotValue(int slot) => _slotValue[slot];

        /// <summary>
        /// Reserve <paramref name="sizeBytes"/> bytes for one upload, aligned to <see cref="CopyAlignment"/>.
        /// <para>
        /// SUB-ALLOCATES FIRST AND CREATES LAST: an open block with room takes the lease by bumping, so a run of
        /// small uploads costs no native call at all after the first. A request that fits in no open block takes
        /// the smallest pooled block that fits, and only a request that fits in neither creates one.
        /// </para>
        /// </summary>
        /// <param name="sizeBytes">The payload size, ALREADY rounded up to <see cref="CopyAlignment"/> by the
        /// caller (<see cref="AlignedCopyBytes"/>). Zero is refused, because a copy of nothing is a command
        /// recorded for no reason and the caller has a bug rather than an empty upload, and a size that is not a
        /// multiple of the alignment is refused for the reason below.</param>
        internal MetalStagingLease Take(ulong sizeBytes)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfZero(sizeBytes);

            // THE ALIGNED SIZE IS A PRECONDITION RATHER THAN SOMETHING THIS ROUNDS, and it is what makes a block
            // the exact size of a request enough. Every lease starts at an aligned offset and advances the bump
            // by an aligned amount, so by induction from zero every offset in a block is aligned and no block
            // ever needs slack for the padding. Rounding the block up instead, which is what a caller-agnostic
            // arena would have to do, costs a whole size class on every power-of-two request.
            if (sizeBytes % CopyAlignment != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes,
                    "A native Metal staging lease is taken at a multiple of " + CopyAlignment
                    + " bytes, which is what the copy selector requires of its size on macOS. Round the payload "
                    + "with MetalStagingArena.AlignedCopyBytes before asking for it.");
            }

            List<OpenBlock> open = _open[_slot];

            for (int i = open.Count - 1; i >= 0; i--)
            {
                if (open[i].TryBump(sizeBytes, out MetalStagingLease lease)) return lease;
            }

            var opened = new OpenBlock(TakeBlock(sizeBytes));
            open.Add(opened);

            if (opened.TryBump(sizeBytes, out MetalStagingLease fresh)) return fresh;

            throw new InvalidOperationException(
                "A native Metal staging block of " + opened.Block.SizeBytes + " bytes could not hold a request "
                + "of " + sizeBytes + " bytes. A fresh block starts at offset 0, which is aligned to everything, "
                + "so this is arithmetic that cannot be reached rather than a machine that ran out of memory.");
        }

        /// <summary>
        /// OPEN <paramref name="slot"/>: give back the blocks it filled last time round IF the GPU has finished
        /// with them, apply the retention cap, and sub-allocate here from now on. Called by
        /// <c>MetalCommandList.Begin</c> immediately after the ring's segment gate, with the completion value
        /// that gate already read.
        /// <para>
        /// A SLOT WHOSE VALUE HAS NOT BEEN REACHED KEEPS ITS BLOCKS AND THIS RETURNS ANYWAY. Handing them back
        /// would hand back memory a submitted blit is still reading, which is the same class of corruption the
        /// ring's segment gate exists to prevent arriving through the other path, and waiting for it would put a
        /// second source into MM4's stall count. The blocks are given back at a later visit to the same slot, so
        /// nothing leaks.
        /// </para>
        /// <para>
        /// THE CAP RELEASES THE LARGEST BLOCKS FIRST, which is the direction that keeps the pool useful: the
        /// small classes are the ones a load revisits thousands of times, and the one enormous block a single
        /// vertex stream needed is the one worth giving back.
        /// </para>
        /// </summary>
        /// <param name="slot">The slot to open, which is the ring segment the list's <c>Begin</c> just
        /// acquired.</param>
        /// <param name="completedValue">The device timeline's completion value, read once by the segment
        /// gate.</param>
        internal void BeginSlot(int slot, ulong completedValue)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (slot < 0 || slot >= _open.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    "A native Metal staging arena has " + _open.Length + " slots, matching the uniform ring the "
                    + "list that owns it rotates with.");
            }

            _slot = slot;

            if (_slotValue[slot] != 0 && completedValue < _slotValue[slot]) return;

            List<OpenBlock> open = _open[slot];
            for (int i = 0; i < open.Count; i++) Retain(open[i].Block);
            open.Clear();

            _slotValue[slot] = 0;
            TrimToCap();
        }

        /// <summary>
        /// The list submitted a recording that signals <paramref name="value"/>, so everything leased into the
        /// CURRENT slot since its last recycle is read by that submission and may not be reused until the
        /// timeline reaches it. Called by <c>MetalCommandList.MarkSubmitted</c> and by nothing else.
        /// <para>
        /// THE HIGHEST VALUE WINS, because a slot can carry more than one submission: a list re-Begun without the
        /// ring having wrapped stays on the same slot, and the blocks from both recordings are then read by both
        /// submissions. Taking the maximum is what keeps one gate sufficient for all of them.
        /// </para>
        /// </summary>
        internal void RecordSubmitted(ulong value)
        {
            if (_disposed) return;
            if (value > _slotValue[_slot]) _slotValue[_slot] = value;
        }

        /// <summary>
        /// Release every block, open and pooled. The arena dies with its list.
        /// <para>
        /// SAFE WITH WORK IN FLIGHT, which is the property that makes this a plain loop rather than a deferred
        /// teardown (M-H3). An <c>MTLCommandBuffer</c> retains every resource its encoders reference until it
        /// completes, so releasing a block a submitted blit still names drops this arena's reference and nothing
        /// else. That is the same reason there is no retire list anywhere in this backend.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            for (int slot = 0; slot < _open.Length; slot++)
            {
                List<OpenBlock> open = _open[slot];
                for (int i = 0; i < open.Count; i++) Destroy(open[i].Block);
                open.Clear();
            }

            for (int i = 0; i < _free.Count; i++) Destroy(_free[i]);
            _free.Clear();
            _retained = 0;
        }

        /// <summary>The block size a request of <paramref name="sizeBytes"/> lands in: the next power of two at
        /// or above it, floored at <paramref name="blockBytes"/>. The pool key, and the reason a pool has classes
        /// rather than one entry per distinct upload size.</summary>
        internal static ulong BlockSizeFor(ulong sizeBytes, ulong blockBytes)
        {
            if (sizeBytes <= blockBytes) return blockBytes;

            ulong size = blockBytes;
            while (size < sizeBytes)
            {
                // A doubling that would overflow means a request past half the address space, so the exact size
                // is the only honest answer and the driver refuses it rather than this line pretending.
                if (size > ulong.MaxValue / 2) return sizeBytes;
                size *= 2;
            }

            return size;
        }

        /// <summary>The payload size a copy of <paramref name="sizeBytes"/> bytes actually moves: rounded up to
        /// <see cref="CopyAlignment"/>. Section 9.3 keeps the size-rounding half of the incumbent's own
        /// <c>CopyBuffer</c> handling, and <see cref="MetalBufferPolicy.AllocationBytes"/> is what makes the pad
        /// land inside the destination's allocation rather than past its end.</summary>
        internal static uint AlignedCopyBytes(uint sizeBytes)
            => sizeBytes + ((uint)CopyAlignment - (sizeBytes % (uint)CopyAlignment)) % (uint)CopyAlignment;

        // The smallest pooled block that fits, or a fresh one. No slack is added for the alignment, because
        // Take's precondition already keeps every offset in a block aligned. See that member.
        MetalStagingBlock TakeBlock(ulong sizeBytes)
        {
            for (int i = 0; i < _free.Count; i++)
            {
                if (_free[i].SizeBytes < sizeBytes) continue;

                MetalStagingBlock pooled = _free[i];
                _free.RemoveAt(i);
                _retained -= pooled.SizeBytes;
                return pooled;
            }

            ulong blockBytes = BlockSizeFor(sizeBytes, _blockBytes);
            MetalStagingBlock block = _source.Create(blockBytes);
            BlocksCreated++;

            if (!block.IsValid)
            {
                throw new InvalidOperationException(
                    "The native Metal device would not allocate a " + blockBytes
                    + "-byte Shared staging block for a record-time buffer upload. "
                    + "-newBufferWithLength:options: answers nil only when the allocation itself fails, so this "
                    + "is memory pressure rather than a malformed request.");
            }

            return block;
        }

        void Retain(in MetalStagingBlock block)
        {
            // Smallest first, so TakeBlock's first fit is also its best fit and a 4 MiB block is never carved up
            // for a 200-byte upload while a 64 KiB one sits idle behind it.
            int at = 0;
            while (at < _free.Count && _free[at].SizeBytes < block.SizeBytes) at++;

            _free.Insert(at, block);
            _retained += block.SizeBytes;
        }

        void TrimToCap()
        {
            while (_retained > _retentionBytes && _free.Count > 0)
            {
                int last = _free.Count - 1;
                MetalStagingBlock biggest = _free[last];
                _free.RemoveAt(last);
                _retained -= biggest.SizeBytes;
                Destroy(biggest);
            }
        }

        void Destroy(in MetalStagingBlock block)
        {
            _source.Destroy(block);
            BlocksDestroyed++;
        }

        // One block plus how far it has been bumped. A class rather than a struct because the list holds it and a
        // bump has to be visible to the next Take through that list.
        sealed class OpenBlock
        {
            ulong _used;

            internal OpenBlock(MetalStagingBlock block) => Block = block;

            internal MetalStagingBlock Block { get; }

            internal bool TryBump(ulong sizeBytes, out MetalStagingLease lease)
            {
                lease = default;

                ulong offset = (_used + (CopyAlignment - 1)) & ~(CopyAlignment - 1);
                if (offset < _used) return false;
                if (offset > Block.SizeBytes || sizeBytes > Block.SizeBytes - offset) return false;

                lease = new MetalStagingLease(
                    Block.Buffer, offset, Block.Mapped + (nint)offset, sizeBytes);
                _used = offset + sizeBytes;
                return true;
            }
        }
    }
}
