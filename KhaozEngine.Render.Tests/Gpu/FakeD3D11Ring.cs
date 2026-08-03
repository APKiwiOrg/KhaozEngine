using System;
using System.Runtime.InteropServices;
using System.Threading;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// A constant-buffer ring's memory with no device behind it: a pinned managed array standing in for the
    /// mapped segment, so the engine half of the ring (the segment maths, the map lifecycle, the write offset and
    /// the routing) is driven by plain <c>[Fact]</c>s on macOS and Linux as well as Windows.
    /// <para>
    /// This is why <see cref="ID3D11RingMemory"/> is an interface at all. What is left behind it on the real path
    /// is one <c>Map</c> and one <c>Unmap</c>, and everything that could be wrong about a ring sits above it.
    /// Pinning is what lets a test read the bytes back: the ring writes through a raw pointer, so an unpinned
    /// array would be a collector move away from writing into memory the test is no longer looking at.
    /// </para>
    /// <para>
    /// IT REFUSES A DOUBLE MAP AND A DOUBLE UNMAP by name, because both are the shape of defect the ring's own
    /// bookkeeping exists to prevent and both are silent in production: a second map leaks a mapping the runtime
    /// never gets back, and a second unmap releases one that a later write is still holding a pointer into.
    /// </para>
    /// </summary>
    internal sealed class FakeD3D11RingMemory : ID3D11RingMemory, IDisposable
    {
        readonly byte[] _bytes;
        readonly D3D11EmitterCallLog? _log;
        GCHandle _pin;

        internal FakeD3D11RingMemory(uint totalBytes, D3D11EmitterCallLog? log = null)
        {
            _bytes = new byte[totalBytes];
            _pin = GCHandle.Alloc(_bytes, GCHandleType.Pinned);
            _log = log;
        }

        /// <summary>The whole allocation, segment by segment, exactly as the ring wrote it.</summary>
        internal byte[] Bytes => _bytes;

        /// <summary>Maps taken. Under the deferred driver this is one per ring per submit, which is the floor.
        /// </summary>
        internal int MapCount { get; private set; }

        /// <summary>Mappings released. The other half of that floor.</summary>
        internal int UnmapCount { get; private set; }

        /// <summary>Whether a mapping is outstanding right now.</summary>
        internal bool IsMapped { get; private set; }

        /// <summary>The submit lock the allocator under test was built with, so the fake can record whether a map
        /// arrived holding it. Left null by a test that does not care.</summary>
        internal object? SubmitLock { get; set; }

        /// <summary>Whether the last map ran with <see cref="SubmitLock"/> held. Null until something maps, and
        /// always null while <see cref="SubmitLock"/> is unset, so a test cannot read a false negative out of a
        /// fake it forgot to wire up. A map is a context call, so it owes the lock.</summary>
        internal bool? LastMapHeldTheSubmitLock { get; private set; }

        /// <summary>The same for the last unmap.</summary>
        internal bool? LastUnmapHeldTheSubmitLock { get; private set; }

        /// <summary>What the emitter call log stood at when the last unmap arrived. Zero is the assertion that
        /// matters for a submit: the unmap belongs BEFORE the replay, since a mapped resource cannot be bound to
        /// the pipeline the replay is about to bind it on. Null log leaves it at zero, so a test that cares wires
        /// the log in.</summary>
        internal int EmitterCallsAtLastUnmap { get; private set; }

        /// <summary>
        /// Whether the CALLER of the last unmap already held the submit lock, as opposed to the unmap having
        /// taken the lock for itself. That is a different question from
        /// <see cref="LastUnmapHeldTheSubmitLock"/>, which is true either way, and it is the one a submit's
        /// bracket turns on: an unmap that takes the lock for itself RELEASES it on the way out, and an
        /// off-timeline write arriving in the gap before the replay re-acquires it maps the ring straight back.
        /// Null until something unmaps, and always null while <see cref="SubmitLock"/> is unset.
        /// </summary>
        internal bool? LastUnmapWasNestedInTheCallersLock { get; private set; }

        /// <inheritdoc/>
        public IntPtr MapWriteNoOverwrite()
        {
            if (IsMapped)
                throw new InvalidOperationException(
                    "A fake ring memory was mapped while it already held a mapping. In production that leaks a "
                    + "mapping the Direct3D runtime never gets back, so the allocator's double-check under the "
                    + "submit lock exists to make it impossible.");

            MapCount++;
            IsMapped = true;
            if (SubmitLock is object submitLock) LastMapHeldTheSubmitLock = Monitor.IsEntered(submitLock);
            return _pin.AddrOfPinnedObject();
        }

        /// <inheritdoc/>
        public void Unmap()
        {
            if (!IsMapped)
                throw new InvalidOperationException(
                    "A fake ring memory was unmapped while it held no mapping. In production that releases a "
                    + "mapping something else may still be writing through, so the ring tracks its own flag "
                    + "rather than unmapping whenever asked.");

            UnmapCount++;
            IsMapped = false;
            if (SubmitLock is object submitLock)
            {
                LastUnmapHeldTheSubmitLock = Monitor.IsEntered(submitLock);
                if (LastUnmapHeldTheSubmitLock == true)
                    LastUnmapWasNestedInTheCallersLock = HoldsItMoreThanOnce(submitLock);
            }

            if (_log is not null) EmitterCallsAtLastUnmap = _log.TotalCalls;
        }

        // Whether this thread holds the lock more than once, which is the only way to tell a nested acquisition
        // from an outermost one: a Monitor exposes no recursion count, so the question is asked by releasing one
        // level and asking again, then taking that level straight back. Safe because the caller has just been
        // checked to hold it, and because in the nested case nobody else can take it in between: this thread
        // still owns the outer level. In the outermost case another thread genuinely can, and the re-entry then
        // waits for it, which is the ordinary behaviour of the lock rather than a hazard the fake introduces.
        static bool HoldsItMoreThanOnce(object submitLock)
        {
            Monitor.Exit(submitLock);
            bool nested = Monitor.IsEntered(submitLock);
            Monitor.Enter(submitLock);
            return nested;
        }

        /// <summary>The bytes of one segment, for a test that wants to say where a write landed rather than what
        /// the whole allocation looks like.</summary>
        internal ReadOnlySpan<byte> Segment(uint frameBaseBytes, uint length)
            => _bytes.AsSpan((int)frameBaseBytes, (int)length);

        public void Dispose()
        {
            if (_pin.IsAllocated) _pin.Free();
        }
    }

    /// <summary>
    /// One ring, its allocator, its fake memory and the submit lock they share, since every ring test needs the
    /// same four and the wiring between them is what the device row will build. Shared rather than nested in one
    /// test class, because the ring's own behaviour and its segment recycling are two files and both need it.
    /// </summary>
    internal sealed class D3D11RingHarness : IDisposable
    {
        internal D3D11RingHarness(uint sizeInBytes, int framesInFlight,
            D3D11RingMapScope mapScope = D3D11RingMapScope.AcrossRecording,
            D3D11EmitterCallLog? log = null)
        {
            SubmitLock = new object();
            Completion = new FakeD3D11Completion();
            Allocator = new D3D11RingAllocator(framesInFlight, Completion, SubmitLock, mapScope);
            Memory = new FakeD3D11RingMemory(D3D11UniformRing.TotalBytesFor(sizeInBytes, framesInFlight), log);
            Ring = new D3D11UniformRing(Allocator, Memory, sizeInBytes);
        }

        /// <summary>The device's one submit lock, which covers the map and the unmap.</summary>
        internal object SubmitLock { get; }

        /// <summary>The completion timeline the segment gate reads.</summary>
        internal FakeD3D11Completion Completion { get; }

        /// <summary>The device's one ring allocator.</summary>
        internal D3D11RingAllocator Allocator { get; }

        /// <summary>The ring's memory, which is where a test reads back what a write did.</summary>
        internal FakeD3D11RingMemory Memory { get; }

        /// <summary>The ring under test.</summary>
        internal D3D11UniformRing Ring { get; }

        public void Dispose() => Memory.Dispose();
    }

    /// <summary>
    /// A buffer that answers the write path's one question the way a real uniform buffer does. The shipped
    /// <c>D3D11Buffer</c> is Windows-only at the type level (it holds Direct3D objects), so this is what lets the
    /// ROUTING of decision U4 be exercised off Windows: a write to a ring-backed buffer goes into the mapped
    /// segment and records nothing, and a write to anything else takes the recording's payload arena.
    /// </summary>
    internal sealed class FakeRingBackedBuffer : IGpuBuffer, ID3D11RingBacked
    {
        internal FakeRingBackedBuffer(D3D11UniformRing ring)
        {
            Ring = ring;
            SizeInBytes = ring.SizeInBytes;
        }

        /// <inheritdoc/>
        public uint SizeInBytes { get; }

        /// <inheritdoc/>
        public D3D11UniformRing? Ring { get; }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// The completion timeline's read half with nothing behind it, so the ring's segment recycling is testable
    /// without a GPU that could finish work. A test either advances <see cref="Completed"/> by hand or sets
    /// <see cref="CompleteAfterPolls"/> so a stalled acquisition finishes after a known number of polls.
    /// <para>
    /// A test that stalls against neither would spin forever, so the fake counts its polls and throws instead,
    /// turning a hung suite into a named failure. Same treatment, and the same reason, as
    /// <see cref="FakeD3D11FenceTimeline"/>.
    /// </para>
    /// </summary>
    internal sealed class FakeD3D11Completion : ID3D11CompletionRead
    {
        // Generous enough that no legitimate test reaches it, small enough that hitting it is instant.
        internal const int RunawayPollLimit = 10_000;

        /// <summary>What the GPU has reached. Settable, because driving it by hand is how a test pins exactly
        /// which segment is free at which moment.</summary>
        internal ulong Completed { get; set; }

        /// <summary>How many times <see cref="CompletedValue"/> has been read. A segment acquisition that does
        /// not stall costs exactly one.</summary>
        internal int PollCount { get; private set; }

        /// <summary>When set, the timeline jumps to <see cref="CompleteTo"/> once it has been polled this many
        /// times, which is how a test makes a stall end.</summary>
        internal int? CompleteAfterPolls { get; set; }

        /// <summary>What <see cref="CompleteAfterPolls"/> completes to.</summary>
        internal ulong CompleteTo { get; set; }

        /// <inheritdoc/>
        public ulong CompletedValue
        {
            get
            {
                PollCount++;
                if (CompleteAfterPolls is int after && PollCount >= after) Completed = CompleteTo;

                if (CompleteAfterPolls is null && PollCount > RunawayPollLimit)
                {
                    throw new InvalidOperationException(
                        $"A fake completion timeline was polled {PollCount} times without ever reaching the value "
                        + "a ring segment was waiting for. The test is stalling against a timeline whose "
                        + "completion nobody drives, so set CompleteAfterPolls or advance Completed by hand. "
                        + "Failing here rather than spinning, because the alternative is a suite that hangs with "
                        + "no name on it.");
                }

                return Completed;
            }
        }
    }
}
