using System;
using System.Collections.Concurrent;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE CONTENT DEDUPLICATION THAT MAKES M-R9's PIPELINE-SWITCH COMPARISON A HANDLE COMPARE. One per device,
    /// keyed on <see cref="MetalShaderIndexTable.ContentKey"/>. Work-breakdown row 10
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/576).
    ///
    /// <para><b>WITHOUT IT THE COMPARISON IS NEVER EQUAL, which is the incumbent's behaviour this exists to
    /// beat.</b> Metal's argument tables are absolute and per encoder, so a bound resource SURVIVES a pipeline
    /// switch and what a switch can invalidate is only the mapping from an element to an index. M-R9 therefore
    /// invalidates a recorded slot only where the incoming program's index table maps that slot's elements
    /// differently from the outgoing one. Every table built is a fresh object, so without deduplication the
    /// comparison is a reference test that always says "different" and every pipeline switch invalidates
    /// everything, exactly as <c>MTLCommandList.SetPipelineCore</c> already does by clearing its whole active-set
    /// array.</para>
    ///
    /// <para><b>THE KEY IS THE SEAT ROW 9 NAMED AND THERE IS NO SECOND NOTION OF TABLE IDENTITY.</b>
    /// <see cref="MetalShaderIndexTable.ContentKey"/> renders the layout SHAPE and then every entry, which is
    /// exactly the surface the table answers for: <c>TryGetIndex</c> and <c>Entries</c> read the entries, and
    /// <c>RequireLayoutShape</c> compares set count, per-set element count and per-element kind. Two tables with
    /// the same key are therefore interchangeable through every member, which is what makes sharing one instance
    /// sound rather than merely convenient. The shape half is load-bearing and was added after the fact: an
    /// element no stage references contributes no entry at all, so on the entries alone two programs collide
    /// while disagreeing in <c>Layouts</c>, and this cache would then hand pipeline B a table carrying program
    /// A's layouts. Pin 4 would refuse B's own perfectly correct declared array. Loud rather than silent, and
    /// still wrong.</para>
    ///
    /// <para><b>IT IS PER DEVICE AND NOT STATIC.</b> A table is pure data and carries nothing device-shaped, so a
    /// process-wide cache would be safe in content terms and is still wrong: it would outlive every device that
    /// filled it, and it would make two devices' pipelines compare equal through an object neither of them
    /// created. Per device it dies with the device that owns it, which is also the lifetime every shader set
    /// holding one of these already has.</para>
    ///
    /// <para><b>NOTHING IS EVER EVICTED, and that is the invariant rather than an oversight.</b> A table retired
    /// and later rebuilt for the same content would be a DIFFERENT instance, so two pipelines that should
    /// invalidate nothing would start invalidating everything, silently and only for the programs whose shader
    /// sets happened to be disposed. The size is bounded by the number of distinct binding SHAPES a device
    /// compiles, not by the number of shader sets: the shipped catalog is 42 programs, and each table is a small
    /// dictionary plus the reflected layout array it was built against, which is a fraction of the
    /// <c>MTLLibrary</c> pair it arrived with.</para>
    ///
    /// <para><b>LOCK-FREE, because creation is free-threaded and M-W8 says so.</b> A
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> rather than a lock, so this adds no serialisation to a
    /// path the design states has none. Two threads compiling the same program at once both build a table and one
    /// of them wins: the loser's instance is dropped for the GC, both callers get the SAME winner, and the
    /// handle-compare invariant holds through the race, which is the only property that matters here.</para>
    /// </summary>
    internal sealed class MetalIndexTableCache
    {
        readonly ConcurrentDictionary<string, MetalShaderIndexTable> _tables = new(StringComparer.Ordinal);

        /// <summary>How many DISTINCT tables this device has seen. Read by the device-free dedup test as its
        /// census, and useful in a diagnostic line: it is the number of index tables a pipeline switch can
        /// compare against rather than the number of programs compiled.</summary>
        internal int Count => _tables.Count;

        /// <summary>
        /// The canonical instance for <paramref name="table"/>'s content: <paramref name="table"/> itself the
        /// first time that content is seen, and the instance an earlier program produced every time after.
        /// <para>
        /// CALLED AT SHADER-SET CREATION AND NOWHERE ELSE, which is where the table is BUILT (2.2b, pin 6). The
        /// table is a property of the emission, so a pipeline references the one its shader set already carries
        /// and nothing is rebuilt or re-canonicalised at pipeline creation. Deduplicating anywhere later would
        /// mean two shader sets could hand out two instances of one content before anyone asked.
        /// </para>
        /// </summary>
        internal MetalShaderIndexTable Canonical(MetalShaderIndexTable table)
        {
            ArgumentNullException.ThrowIfNull(table);
            return _tables.GetOrAdd(table.ContentKey, table);
        }
    }
}
