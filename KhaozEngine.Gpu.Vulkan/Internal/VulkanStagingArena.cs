using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE PER-LIST STAGING ARENA (V-M9, section 9.3): where a record-time write to a NON-uniform buffer, and a
    /// record-time texture upload, put their bytes on the way to a <c>vkCmdCopyBuffer</c> or a
    /// <c>vkCmdCopyBufferToImage</c>.
    ///
    /// <para><b>THIS IS NOT THE RING AND MUST NOT BECOME IT.</b> Bulk payloads are rare relative to the uniform
    /// sites and they are exactly the traffic the ring is not for: they DO cost a copy command, a barrier and the
    /// render-pass split those copies unavoidably cause. What this type removes is a different cost, below.</para>
    ///
    /// <para><b>THE COST IT REMOVES IS AN ALLOCATION STORM, AND THE INCUMBENT'S CAP IS THE EVIDENCE.</b> The
    /// shipped incumbent pools returned staging buffers and DESTROYS any of them over 512 bytes, so every
    /// real-sized upload creates and destroys a <c>VkBuffer</c> AND a <c>VkDeviceMemory</c> block per call. A
    /// scene load is thousands of those. Raising that to a real cap is not an optimisation, it is removing an
    /// allocation storm from every load. The cap here is <see cref="DefaultRetentionBytes"/> of retained free
    /// blocks, which is a policy number rather than a measured one.</para>
    ///
    /// <para><b>POOLED BY SIZE, WHICH IS WHY THE BLOCK SIZE IS ROUNDED TO A POWER OF TWO.</b> A pool keyed on the
    /// exact byte count would hold one block per distinct upload size and reuse almost nothing, which is a
    /// different way to reach the same storm. Rounding up to a power of two, floored at
    /// <see cref="DefaultBlockBytes"/>, gives a small number of classes that a load actually revisits, and the
    /// slack inside a block is reclaimed by SUB-ALLOCATION rather than wasted: several small uploads share one
    /// block by bumping through it.</para>
    ///
    /// <para><b>RECYCLED PER SLOT, NOT PER COPY AND NOT PER <c>Begin</c>.</b> A block's bytes are read by the GPU
    /// when the copy executes, so returning it at the copy would hand it back while it is still being read. The
    /// safe boundary is the same one the command pool uses: a list's <c>Begin</c> waits for the slot it is
    /// ADVANCING ONTO before it resets that pool (row 7), so the blocks that slot filled LAST TIME ROUND are
    /// provably finished with and nothing else is. That is why the arena keeps one open set per slot rather than
    /// one per list: recycling the whole arena at every <c>Begin</c> would hand back the blocks the PREVIOUS
    /// record's submission is still reading, which is the same class of corruption the ring's fence gate exists to
    /// prevent, arriving through the other path. The arena is PER LIST so it inherits that existing proof instead
    /// of needing a fence of its own.</para>
    ///
    /// <para><b>NOTHING HERE IS THREAD-SAFE, AND THAT IS THE POINT.</b> One arena belongs to one list, and a list
    /// is one thread's at a time (V-R4). Two lists recording on two threads never touch the same arena, so the
    /// record path takes no lock, exactly as the command pools do not.</para>
    ///
    /// <para><b>EVERYTHING ABOVE THE TWO NATIVE CALLS IS DEVICE-FREE</b>, behind
    /// <see cref="IVulkanStagingSource"/>, so the size classes, the sub-allocation, the recycling boundary and the
    /// retention cap all run under <c>dotnet test</c> on a machine with no Vulkan loader.</para>
    /// </summary>
    internal sealed class VulkanStagingArena : IDisposable
    {
        /// <summary>
        /// The smallest block the arena ever creates, 64 KiB.
        /// <para>
        /// Big enough that a run of small uploads shares one block rather than taking one each, and small enough
        /// that an arena holding a handful of them costs nothing measurable. It is a policy number and moving it is
        /// a deliberate edit rather than a drift.
        /// </para>
        /// </summary>
        internal const ulong DefaultBlockBytes = 64UL * 1024;

        /// <summary>
        /// How many bytes of FREE blocks one arena keeps between recyclings, 8 MiB. The "real retention cap" of
        /// V-M9, against the incumbent's 512 bytes.
        /// <para>
        /// It bounds the KEPT blocks rather than the live ones: a single upload larger than this still gets its own
        /// block, it simply is not retained afterwards. Retaining nothing is the incumbent's behaviour and
        /// retaining everything would let a one-off texture load pin its peak for the process's life, so the cap is
        /// the thing that makes "pooled" mean something without making it unbounded.
        /// </para>
        /// </summary>
        internal const ulong DefaultRetentionBytes = 8UL * 1024 * 1024;

        readonly IVulkanStagingSource _source;
        readonly ulong _blockBytes;
        readonly ulong _retentionBytes;

        // Blocks being written into, PER SLOT, with how far each has been bumped. Newest last within a slot,
        // because a run of uploads keeps landing in the block the previous one opened. One set per slot is what
        // makes the recycling boundary the slot wait rather than the Begin.
        readonly List<OpenBlock>[] _open;

        // Blocks returned by a recycle and not yet re-taken, smallest first so a request takes the smallest one
        // that fits rather than carving a large block for a tiny upload.
        readonly List<VulkanStagingBlock> _free = new();

        int _slot;
        ulong _retained;
        bool _disposed;

        /// <param name="source">The two native calls.</param>
        /// <param name="framesInFlight">How many slots the owning list has, from
        /// <see cref="VulkanFramesInFlight"/>. The arena keeps one open set per slot for the reason the class note
        /// gives.</param>
        /// <param name="blockBytes">The smallest block to create. Defaults to
        /// <see cref="DefaultBlockBytes"/>.</param>
        /// <param name="retentionBytes">How many bytes of free blocks to keep across a recycle. Defaults to
        /// <see cref="DefaultRetentionBytes"/>. Zero means keep nothing, which is the incumbent's shape and is
        /// constructible so a test can pin the difference.</param>
        internal VulkanStagingArena(IVulkanStagingSource source, int framesInFlight,
            ulong blockBytes = DefaultBlockBytes, ulong retentionBytes = DefaultRetentionBytes)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentOutOfRangeException.ThrowIfZero(blockBytes);

            if (framesInFlight < VulkanFramesInFlight.Minimum || framesInFlight > VulkanFramesInFlight.Maximum)
            {
                throw new ArgumentOutOfRangeException(nameof(framesInFlight), framesInFlight,
                    $"A native Vulkan staging arena runs between {VulkanFramesInFlight.Minimum} and "
                    + $"{VulkanFramesInFlight.Maximum} slots, matching the command pools of the list that owns it.");
            }

            _source = source;
            _blockBytes = blockBytes;
            _retentionBytes = retentionBytes;

            _open = new List<OpenBlock>[framesInFlight];
            for (int i = 0; i < framesInFlight; i++) _open[i] = new List<OpenBlock>();
        }

        /// <summary>How many slots this arena keeps open sets for.</summary>
        internal int Depth => _open.Length;

        /// <summary>The slot <see cref="Take"/> currently sub-allocates into.</summary>
        internal int Slot => _slot;

        /// <summary>How many blocks this arena has ever asked the source to create. The number the incumbent's
        /// 512-byte cap makes equal to the upload count, and the one MV-style reading of the fix.</summary>
        internal int BlocksCreated { get; private set; }

        /// <summary>How many blocks the retention cap has turned away and destroyed.</summary>
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

        /// <summary>
        /// Reserve <paramref name="sizeBytes"/> bytes for one upload, aligned to <paramref name="alignment"/>.
        /// <para>
        /// SUB-ALLOCATES FIRST AND CREATES LAST: an open block with room takes the lease by bumping, so a run of
        /// small uploads costs no native call at all after the first. A request that fits in no open block takes
        /// the smallest pooled block that fits, and only a request that fits in neither creates one.
        /// </para>
        /// </summary>
        /// <param name="sizeBytes">The payload size. Zero is refused, because a copy of nothing is a command
        /// recorded for no reason and the caller has a bug rather than an empty upload.</param>
        /// <param name="alignment">The offset alignment the copy needs. A non-zero power of two. Four is the
        /// default because <c>vkCmdCopyBuffer</c> asks for nothing more. An image copy passes the device's
        /// <c>optimalBufferCopyOffsetAlignment</c> when that path lands.</param>
        internal VulkanStagingLease Take(ulong sizeBytes, ulong alignment = 4)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentOutOfRangeException.ThrowIfZero(sizeBytes);

            if (!VulkanMemoryFreeList.IsPowerOfTwo(alignment))
            {
                throw new ArgumentOutOfRangeException(nameof(alignment), alignment,
                    "A native Vulkan staging alignment must be a non-zero power of two.");
            }

            List<OpenBlock> open = _open[_slot];

            for (int i = open.Count - 1; i >= 0; i--)
            {
                if (open[i].TryBump(sizeBytes, alignment, out VulkanStagingLease lease))
                {
                    return lease;
                }
            }

            var opened = new OpenBlock(TakeBlock(sizeBytes, alignment));
            open.Add(opened);

            if (opened.TryBump(sizeBytes, alignment, out VulkanStagingLease fresh)) return fresh;

            throw new InvalidOperationException(
                "A native Vulkan staging block of "
                + opened.Block.SizeBytes.ToString(CultureInfo.InvariantCulture)
                + " bytes could not hold a request of "
                + sizeBytes.ToString(CultureInfo.InvariantCulture)
                + " bytes at alignment "
                + alignment.ToString(CultureInfo.InvariantCulture)
                + ". A fresh block starts at offset 0, which is aligned to everything, so this is arithmetic that "
                + "cannot be reached rather than a machine that ran out of memory.");
        }

        /// <summary>
        /// OPEN <paramref name="slot"/>: give back the blocks it filled LAST time round, apply the retention cap,
        /// and sub-allocate here from now on. Called by the list's <c>Begin</c> immediately after the pool ring has
        /// advanced onto that slot, which is AFTER it waited for that slot's last submission, so the blocks handed
        /// back are provably finished with and no other slot's are touched.
        /// <para>
        /// THE CAP DESTROYS THE LARGEST BLOCKS FIRST, which is the direction that keeps the pool useful: the small
        /// classes are the ones a load revisits thousands of times, and the one enormous block a single texture
        /// needed is the one worth giving back.
        /// </para>
        /// </summary>
        internal void BeginSlot(int slot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (slot < 0 || slot >= _open.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(slot), slot,
                    "A native Vulkan staging arena has "
                    + _open.Length.ToString(CultureInfo.InvariantCulture)
                    + " slots, matching the command pools of the list that owns it.");
            }

            List<OpenBlock> open = _open[slot];
            for (int i = 0; i < open.Count; i++) Retain(open[i].Block);
            open.Clear();

            TrimToCap();

            _slot = slot;
        }

        /// <summary>Destroy every block, open and pooled. The arena dies with its list, and its blocks are the
        /// list's for the same reason its command pools are.</summary>
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

        /// <summary>The block size a request of <paramref name="sizeBytes"/> lands in: the next power of two at or
        /// above it, floored at <paramref name="blockBytes"/>. The pool key, and the reason a pool has classes
        /// rather than one entry per distinct upload size.</summary>
        internal static ulong BlockSizeFor(ulong sizeBytes, ulong blockBytes)
        {
            if (sizeBytes <= blockBytes) return blockBytes;

            ulong size = blockBytes;
            while (size < sizeBytes)
            {
                // A doubling that would overflow means a request past half the address space, so the exact size is
                // the only honest answer and the driver refuses it rather than this line pretending.
                if (size > ulong.MaxValue / 2) return sizeBytes;
                size *= 2;
            }

            return size;
        }

        // The smallest pooled block that fits, or a fresh one. The alignment is folded into the requirement rather
        // than checked afterwards, so a block that fits the payload but not the padding is not taken and then
        // found wanting.
        VulkanStagingBlock TakeBlock(ulong sizeBytes, ulong alignment)
        {
            ulong needed = sizeBytes + (alignment - 1);
            if (needed < sizeBytes) needed = sizeBytes;

            for (int i = 0; i < _free.Count; i++)
            {
                if (_free[i].SizeBytes < needed) continue;

                VulkanStagingBlock pooled = _free[i];
                _free.RemoveAt(i);
                _retained -= pooled.SizeBytes;
                return pooled;
            }

            VulkanStagingBlock block = _source.Create(BlockSizeFor(needed, _blockBytes));
            BlocksCreated++;

            if (!block.IsValid)
            {
                throw new InvalidOperationException(
                    "A native Vulkan staging source returned a block with no buffer or no mapping. Staging memory "
                    + "is host-visible and mapped once at chunk creation (V-M3), so an unmapped block means the "
                    + "allocation came from a device-local type.");
            }

            return block;
        }

        void Retain(in VulkanStagingBlock block)
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
                VulkanStagingBlock biggest = _free[last];
                _free.RemoveAt(last);
                _retained -= biggest.SizeBytes;
                Destroy(biggest);
            }
        }

        void Destroy(in VulkanStagingBlock block)
        {
            _source.Destroy(block);
            BlocksDestroyed++;
        }

        // One block plus how far it has been bumped. A class rather than a struct because the list holds it and a
        // bump has to be visible to the next Take through that list.
        sealed class OpenBlock
        {
            ulong _used;

            internal OpenBlock(VulkanStagingBlock block) => Block = block;

            internal VulkanStagingBlock Block { get; }

            internal bool TryBump(ulong sizeBytes, ulong alignment, out VulkanStagingLease lease)
            {
                lease = default;

                if (!VulkanMemoryFreeList.TryAlignUp(_used, alignment, out ulong offset)) return false;
                if (offset > Block.SizeBytes || sizeBytes > Block.SizeBytes - offset) return false;

                lease = new VulkanStagingLease(
                    Block.Buffer, offset, Block.Mapped + (nint)offset, sizeBytes);
                _used = offset + sizeBytes;
                return true;
            }
        }
    }
}
