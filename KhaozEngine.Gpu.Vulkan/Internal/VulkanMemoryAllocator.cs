using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE ENGINE-OWNED BLOCK SUBALLOCATOR of decision V-M1, and the device's one instance of it. Section 9.1.
    ///
    /// <para><b>WHAT IT IS.</b> Chunks of a fixed size, one <c>vkAllocateMemory</c> each, pooled by
    /// <c>(memoryTypeIndex, linear|optimal)</c>. First-fit over a sorted free list with alignment correction,
    /// split on allocate, merge with both neighbours on free (<see cref="VulkanMemoryFreeList"/>). Memory types
    /// chosen off preference ladders (<see cref="VulkanMemoryTypeSelection"/>). Host-visible chunks mapped once at
    /// creation and never unmapped (<see cref="VulkanMemoryChunk"/>). Dedicated allocations when the driver asks
    /// or the request is large. One short lock around allocate and free, because allocation is not on the hot
    /// path.</para>
    ///
    /// <para><b>WHY NOT VMA, and the CONDITION on that answer (V-M1).</b> VMA is a C++ library with no maintained
    /// managed binding, so the real proposal is a native binary per RID added to a backend whose premise is
    /// reducing native surface. The workload has no allocation problem to solve: meshes and textures allocate at
    /// load, uniform rings allocate once at creation, and the steady-state frame allocates NOTHING. The
    /// counterargument owed is that hand-rolled allocators are where memory corruption lives and the failure mode
    /// is an aliasing bug no test on a software rasterizer will show. The answer is this code's readability plus
    /// row 19's synchronisation-validation job (https://github.com/APKiwiOrg/KhaozEngine/issues/529), which is the
    /// only instrument in the net that sees aliasing and hazard errors. <b>That linkage is a decision rather than
    /// a remark: if the sync-validation gate is ever dropped, the VMA decline has to be re-argued.</b></para>
    ///
    /// <para><b>THE LOCK IS HELD ACROSS <c>vkAllocateMemory</c> WHEN A CHUNK IS CREATED, on purpose.</b> The
    /// tempting alternative is to allocate the new chunk outside the lock and insert it afterwards, which lets two
    /// threads that both missed create two chunks for one pool and waste a whole chunk. Allocation is off the hot
    /// path by design (V-M2) and a chunk creation is rare, so serialising it costs nothing measurable and removes
    /// a class of waste that is invisible until a memory reading looks wrong.</para>
    ///
    /// <para><b>EVERYTHING ABOVE THE FIVE NATIVE CALLS IS DEVICE-FREE</b>, behind
    /// <see cref="IVulkanDeviceMemoryApi"/>, so the pooling, the ladders, the splitting, the coalescing, the
    /// dedicated path, the counter and the retire ordering all run under <c>dotnet test</c> on a machine with no
    /// Vulkan loader. That matters more here than anywhere else in this backend, for the reason the VMA
    /// counterargument above gives.</para>
    /// </summary>
    internal sealed class VulkanMemoryAllocator : IDisposable
    {
        static readonly ILogger log = Log.For<VulkanMemoryAllocator>();

        /// <summary>
        /// The default chunk size: 64 MiB.
        /// <para>
        /// Big enough that an ordinary scene's meshes and textures land a few dozen suballocations per chunk, and
        /// small enough that a pool holding one resident empty chunk per <c>(type, tiling)</c> pair is not holding
        /// a quarter of a small card's VRAM. It is a policy number rather than a measured one, and MV6's resident
        /// memory reading against the incumbent on the same scene is what would move it.
        /// </para>
        /// </summary>
        internal const ulong DefaultChunkSize = 64UL * 1024 * 1024;

        /// <summary>
        /// The default dedicated-allocation threshold: a quarter of a chunk, 16 MiB.
        /// <para>
        /// A request at or above this either does not fit beside much else or leaves a hole nothing else fits in,
        /// so pooling it trades one <c>vkAllocateMemory</c> for fragmentation that outlives the allocation. Below
        /// it, pooling is strictly better. The number is a policy choice like the chunk size, and it is pinned by
        /// a test so moving it is a deliberate edit rather than a drift.
        /// </para>
        /// </summary>
        internal const ulong DefaultDedicatedThreshold = DefaultChunkSize / 4;

        readonly IVulkanDeviceMemoryApi _api;
        readonly IVulkanMemoryRetirement _retire;
        readonly VulkanMemoryFacts _facts;
        readonly ILogger _log;
        readonly ulong _chunkSize;
        readonly ulong _dedicatedThreshold;

        readonly object _gate = new();
        readonly Dictionary<PoolKey, List<VulkanMemoryChunk>> _pools = new();
        readonly List<VulkanMemoryChunk> _dedicated = new();

        long _live;
        long _lifetime;
        bool _budgetWarned;
        bool _closed;

        /// <param name="api">The five native calls.</param>
        /// <param name="facts">The device's memory types and the two limits that shape the arithmetic.</param>
        /// <param name="retire">Where a chunk's <c>vkFreeMemory</c> is handed so it runs only after the timeline
        /// has passed the value recorded at free time (V-F9).</param>
        /// <param name="chunkSize">Bytes per pooled chunk. Defaults to <see cref="DefaultChunkSize"/>.</param>
        /// <param name="dedicatedThreshold">The size at or above which a request gets its own
        /// <c>vkAllocateMemory</c>. Defaults to <see cref="DefaultDedicatedThreshold"/>.</param>
        /// <param name="logger">The sink, or null for this type's own category logger.</param>
        internal VulkanMemoryAllocator(IVulkanDeviceMemoryApi api, VulkanMemoryFacts facts,
            IVulkanMemoryRetirement retire, ulong chunkSize = DefaultChunkSize,
            ulong dedicatedThreshold = DefaultDedicatedThreshold, ILogger? logger = null)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(retire);
            ArgumentOutOfRangeException.ThrowIfZero(chunkSize);
            ArgumentOutOfRangeException.ThrowIfZero(dedicatedThreshold);

            _api = api;
            _facts = facts;
            _retire = retire;
            _chunkSize = chunkSize;
            _dedicatedThreshold = dedicatedThreshold;
            _log = logger ?? log;
        }

        /// <summary>
        /// HOW MANY <c>vkAllocateMemory</c> RESULTS ARE LIVE RIGHT NOW, which is the first half of the reading
        /// bet MV6 is settled on: the allocation count against the device's <c>maxMemoryAllocationCount</c>, with
        /// the exit criterion being under a quarter of it.
        /// <para>
        /// DELIBERATELY NOT ON <c>GpuDeviceCounters</c>. That struct has no allocation-count field, and widening
        /// a cross-backend seam for one backend's internal reading is the wrong direction: Direct3D 11 and Metal
        /// have nothing to put in it. Row 18 (https://github.com/APKiwiOrg/KhaozEngine/issues/528) owns the
        /// capability and counter surface and is where this becomes a reported number if it ever should be. Until
        /// then it is read here, by the soak session that takes MV6's reading.
        /// </para>
        /// </summary>
        internal long LiveDeviceAllocations => Interlocked.Read(ref _live);

        /// <summary>How many <c>vkAllocateMemory</c> calls this allocator has EVER made. The second half of MV6's
        /// reading: a live count that stays flat while this climbs is a pool that is churning chunks, which is a
        /// different problem from a pool that is simply large.</summary>
        internal long LifetimeDeviceAllocations => Interlocked.Read(ref _lifetime);

        /// <summary>How many distinct <c>(memoryTypeIndex, tiling)</c> pools exist. A number that reflects how
        /// many memory types the workload actually touches, and the thing a reader checks first when resident
        /// memory looks high.</summary>
        internal int PoolCount
        {
            get { lock (_gate) return _pools.Count; }
        }

        /// <summary>How many chunks are dedicated rather than pooled.</summary>
        internal int DedicatedChunkCount
        {
            get { lock (_gate) return _dedicated.Count; }
        }

        /// <summary>
        /// Allocate memory for one resource.
        /// </summary>
        /// <param name="request">The requirements, translated out of <c>VkMemoryRequirements</c> and
        /// <c>VkMemoryDedicatedRequirements</c>.</param>
        /// <exception cref="InvalidOperationException">No memory type satisfies the usage within the request's own
        /// type mask, or the allocator has already been torn down.</exception>
        internal VulkanMemoryAllocation Allocate(in VulkanMemoryRequest request)
        {
            if (request.Size == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.Size,
                    "The native Vulkan allocator was asked for zero bytes. vkAllocateMemory rejects an "
                    + "allocationSize of 0, and a suballocation of 0 has no offset that means anything.");
            }

            if (!VulkanMemoryFreeList.IsPowerOfTwo(request.Alignment))
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.Alignment,
                    "A native Vulkan memory alignment must be a non-zero power of two, which every "
                    + "VkMemoryRequirements.alignment is by spec.");
            }

            int chosen = VulkanMemoryTypeSelection.Choose(
                request.Usage, request.MemoryTypeBits, _facts.Types, out int rung);

            if (chosen == VulkanMemoryTypeSelection.NoType)
            {
                throw new InvalidOperationException(VulkanMemoryTypeSelection.Unsatisfiable(
                    request.Usage, request.MemoryTypeBits, _facts.Types));
            }

            VulkanMemoryTrait traits = TraitsOf((uint)chosen);
            ulong reserved = VulkanMemoryChunk.SizeFor(request.Size, traits, _facts.NonCoherentAtomSize);

            lock (_gate)
            {
                RequireOpen();

                return WantsDedicated(request, reserved)
                    ? AllocateDedicated(request, (uint)chosen, traits, reserved)
                    : AllocatePooled(request, (uint)chosen, traits, reserved, rung);
            }
        }

        /// <summary>
        /// Give an allocation back. The bytes return to their chunk's free list immediately, and the CHUNK is
        /// retired (not freed) when it empties, so its <c>vkFreeMemory</c> runs only once the timeline has passed
        /// the value recorded here.
        /// <para>
        /// A POOL KEEPS ITS LAST CHUNK. Retiring the only chunk of a pool that is about to be used again turns a
        /// load-unload cycle into a <c>vkAllocateMemory</c> per iteration, which is exactly the allocation storm
        /// this allocator exists to remove. A dedicated chunk has no such argument and is always retired: it
        /// exists for one resource and that resource has gone.
        /// </para>
        /// <para>
        /// A FREE AFTER TEARDOWN IS A NO-OP rather than a throw, because a resource wrapper outliving its device
        /// is ordinary at teardown and its chunk's memory has already gone with the device. That is the same rule
        /// <see cref="VulkanDeviceLiveness"/> applies to every other destroy in this package.
        /// </para>
        /// </summary>
        internal void Free(in VulkanMemoryAllocation allocation)
        {
            if (!allocation.IsValid)
            {
                throw new ArgumentException(
                    "A default VulkanMemoryAllocation has no chunk behind it, so there is nothing to free. The "
                    + "allocator throws on failure rather than returning one, so reaching this means a value was "
                    + "stored before it was made.", nameof(allocation));
            }

            VulkanMemoryChunk chunk = allocation.Chunk!;
            VulkanMemoryChunk? retiring = null;

            lock (_gate)
            {
                if (_closed || chunk.IsDestroyed) return;

                chunk.Free(allocation.Offset);

                if (chunk.IsDedicated)
                {
                    _dedicated.Remove(chunk);
                    retiring = chunk;
                }
                else if (chunk.IsEmpty
                    && _pools.TryGetValue(new PoolKey(chunk.MemoryTypeIndex, chunk.Tiling), out var chunks)
                    && chunks.Count > 1)
                {
                    chunks.Remove(chunk);
                    retiring = chunk;
                }
            }

            // OUTSIDE THE LOCK. The retire list takes its own, and holding two locks in one order here would put
            // an ordering constraint on every other pair of callers for no benefit.
            if (retiring != null) RetireChunk(retiring);
        }

        /// <summary>
        /// Free every chunk, immediately and natively. The device's teardown path, called after
        /// <c>vkDeviceWaitIdle</c> has returned and before the liveness flip, which is the only window in which
        /// destroying a child object of the device is both safe and legal.
        /// <para>
        /// IMMEDIATE RATHER THAN RETIRED, because the wait above is what makes it safe: the GPU is idle, so no
        /// submission can still be reading any of it, and handing these to the retire list would only mean the
        /// same calls one drain later. Chunks already handed to the retire list are NOT here: they left their
        /// pool at the moment they were retired, and their destroy is idempotent, so the two paths cannot both
        /// free the same object.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;

                Release(static chunk => chunk.Destroy());
            }
        }

        /// <summary>
        /// Drop every chunk WITHOUT freeing it, for a device that is already dead or was lost by the teardown
        /// wait. Its memory went with the device, so a <c>vkFreeMemory</c> now is a call against freed memory,
        /// which aborts the process through the Vulkan loader rather than failing quietly. The allocator's
        /// counterpart to <see cref="VulkanRetireList.Abandon"/>.
        /// </summary>
        /// <returns>How many chunks were dropped, for the report line.</returns>
        internal int Abandon()
        {
            lock (_gate)
            {
                if (_closed) return 0;
                _closed = true;

                return Release(static chunk => chunk.Forget());
            }
        }

        /// <summary>The INFO line taken at device creation: the policy numbers and the machine's own limits, so a
        /// soak log carries what MV6's reading has to be interpreted against.</summary>
        internal string Describe()
        {
            string chunk = (_chunkSize / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
            string threshold = (_dedicatedThreshold / (1024 * 1024)).ToString(CultureInfo.InvariantCulture);
            string types = _facts.Types.Count.ToString(CultureInfo.InvariantCulture);
            string atom = _facts.NonCoherentAtomSize.ToString(CultureInfo.InvariantCulture);
            string limit = _facts.MaxAllocationCount.ToString(CultureInfo.InvariantCulture);

            return $"The native Vulkan allocator is up: {chunk} MiB chunks pooled by (memory type, tiling), "
                + $"dedicated at or above {threshold} MiB or on driver preference, {types} memory types reported, "
                + $"nonCoherentAtomSize {atom}, maxMemoryAllocationCount {limit}.";
        }

        // The DEDICATED path: one chunk sized to this request, outside the pools. It still goes through the same
        // chunk and the same free list, so there is no second allocation path that nothing exercises: the chunk is
        // exactly the reserved size, so the one suballocation fills it and a second cannot fit.
        VulkanMemoryAllocation AllocateDedicated(in VulkanMemoryRequest request, uint typeIndex,
            VulkanMemoryTrait traits, ulong reserved)
        {
            VulkanMemoryChunk chunk = NewChunk(
                typeIndex, traits, request.Tiling, reserved, request.DedicatedTarget, isDedicated: true);

            _dedicated.Add(chunk);

            if (chunk.TryAllocate(request.Size, request.Alignment, out VulkanMemoryAllocation allocation))
                return allocation;

            throw Unfittable(request, reserved, dedicated: true);
        }

        // The POOLED path: first fit across the pool's existing chunks in creation order, then a fresh chunk.
        VulkanMemoryAllocation AllocatePooled(in VulkanMemoryRequest request, uint typeIndex,
            VulkanMemoryTrait traits, ulong reserved, int rung)
        {
            var key = new PoolKey(typeIndex, request.Tiling);

            if (!_pools.TryGetValue(key, out List<VulkanMemoryChunk>? chunks))
            {
                chunks = new List<VulkanMemoryChunk>();
                _pools.Add(key, chunks);

                _log.Info($"The native Vulkan allocator opened a pool for memory type {typeIndex} ({traits}) with "
                    + $"{request.Tiling} tiling, chosen at rung {rung} of the {request.Usage} ladder. Linear and "
                    + "optimal tiling never share a chunk, which is how bufferImageGranularity is satisfied here "
                    + "without any granularity arithmetic.");
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                if (chunks[i].TryAllocate(request.Size, request.Alignment, out VulkanMemoryAllocation existing))
                    return existing;
            }

            // A chunk is never smaller than the request, even though the dedicated threshold above should already
            // have taken anything that large. Max rather than an assertion, because a caller-supplied chunk size
            // below the threshold is legal and this is the one line that would silently mis-size a chunk.
            ulong size = Math.Max(_chunkSize, reserved);
            VulkanMemoryChunk fresh = NewChunk(
                typeIndex, traits, request.Tiling, size, VulkanDedicatedTarget.None, isDedicated: false);

            chunks.Add(fresh);

            if (fresh.TryAllocate(request.Size, request.Alignment, out VulkanMemoryAllocation allocation))
                return allocation;

            throw Unfittable(request, reserved, dedicated: false);
        }

        // The ONE place vkAllocateMemory happens, and therefore the one place the counter moves up.
        VulkanMemoryChunk NewChunk(uint typeIndex, VulkanMemoryTrait traits, VulkanMemoryTiling tiling, ulong size,
            VulkanDedicatedTarget dedicated, bool isDedicated)
        {
            var chunk = new VulkanMemoryChunk(
                _api, typeIndex, traits, tiling, size, _facts.NonCoherentAtomSize, dedicated, isDedicated);

            Interlocked.Increment(ref _lifetime);
            WarnIfNearTheAllocationLimit(Interlocked.Increment(ref _live));
            return chunk;
        }

        void RetireChunk(VulkanMemoryChunk chunk)
        {
            _retire.Retire(() =>
            {
                if (chunk.Destroy()) Interlocked.Decrement(ref _live);
            });
        }

        // Shared by Dispose and Abandon: end every chunk with the given form, decrementing the live count for
        // each one this call actually ended, and leave the pools empty.
        int Release(Func<VulkanMemoryChunk, bool> end)
        {
            int ended = 0;

            foreach (List<VulkanMemoryChunk> chunks in _pools.Values)
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (end(chunks[i])) ended++;
                }
            }

            for (int i = 0; i < _dedicated.Count; i++)
            {
                if (end(_dedicated[i])) ended++;
            }

            Interlocked.Add(ref _live, -ended);
            _pools.Clear();
            _dedicated.Clear();
            return ended;
        }

        // THREE REASONS, and the first two are the driver's. requiresDedicatedAllocation is a spec requirement
        // rather than a hint. prefersDedicatedAllocation is a hint the driver only gives when it has a compression
        // or fast-clear path it can take on memory it owns outright, so ignoring it trades a real win for a
        // suballocation nobody needed. The third is this allocator's own: a request at or above the threshold
        // fragments a chunk more than it saves.
        bool WantsDedicated(in VulkanMemoryRequest request, ulong reserved)
            => request.DriverWantsDedicated || reserved >= _dedicatedThreshold || reserved > _chunkSize;

        VulkanMemoryTrait TraitsOf(uint index)
        {
            for (int i = 0; i < _facts.Types.Count; i++)
            {
                if (_facts.Types[i].Index == index) return _facts.Types[i].Traits;
            }

            throw new InvalidOperationException(
                "The native Vulkan allocator chose memory type "
                + index.ToString(CultureInfo.InvariantCulture)
                + ", which is not in the type list it chose from. The selection returns an index out of that same "
                + "list, so this cannot happen without the list having been mutated underneath it.");
        }

        // ONE WARNING PER DEVICE, at MV6's own exit criterion, so a soak session that is going to fail the gate
        // says so while it runs rather than only in the reading afterwards.
        void WarnIfNearTheAllocationLimit(long live)
        {
            if (_budgetWarned || _facts.MaxAllocationCount == 0) return;
            if ((ulong)live * 4 < _facts.MaxAllocationCount) return;

            _budgetWarned = true;
            _log.Warn($"The native Vulkan backend now holds {live.ToString(CultureInfo.InvariantCulture)} live "
                + "vkAllocateMemory results, which is at or above a quarter of this device's "
                + $"maxMemoryAllocationCount of {_facts.MaxAllocationCount.ToString(CultureInfo.InvariantCulture)}"
                + ". A quarter is the exit criterion of measurement gate MV6, so this run would fail that gate as "
                + "it stands. Said once per device, not once per allocation.");
        }

        void RequireOpen()
        {
            if (!_closed) return;

            throw new InvalidOperationException(
                "The native Vulkan allocator was asked for memory after it was torn down. Its chunks have been "
                + "freed (or went with a dead device), so there is nothing left to suballocate out of. A resource "
                + "created after its device was disposed is the shape that reaches this.");
        }

        InvalidOperationException Unfittable(in VulkanMemoryRequest request, ulong reserved, bool dedicated)
            => new("The native Vulkan allocator created a "
                + (dedicated ? "dedicated" : "fresh pooled")
                + " chunk of at least "
                + reserved.ToString(CultureInfo.InvariantCulture)
                + " bytes and it still could not hold a request of "
                + request.Size.ToString(CultureInfo.InvariantCulture)
                + " bytes at alignment "
                + request.Alignment.ToString(CultureInfo.InvariantCulture)
                + ". A chunk's first free range starts at offset 0, which is aligned to everything, so this is "
                + "arithmetic that cannot be reached rather than a machine that ran out of memory.");

        /// <summary>The pool key of decision V-M2. Tiling is half of it, which is the entire
        /// <c>bufferImageGranularity</c> implementation.</summary>
        readonly record struct PoolKey(uint MemoryTypeIndex, VulkanMemoryTiling Tiling);
    }
}
