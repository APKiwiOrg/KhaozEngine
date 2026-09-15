using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.Tests.ItemInstances.Allocator;

/// <summary>
/// The host's durable store, faked, with the one property the order fact needs: it records every persist
/// in the same list the test records every issue into, so the INTERLEAVING is observable rather than
/// inferred. Contracts 6.2 makes the order the contract, and a batching optimisation is where it gets
/// quietly inverted, so the test has to see both sides of the interleave.
/// </summary>
public sealed class RecordingInstanceIdStore : IInstanceIdStore
{
    readonly List<string> _log = new();
    InstanceIdState _state;

    /// <param name="state">What a boot reads. The default value is a fresh store.</param>
    public RecordingInstanceIdStore(InstanceIdState state = default) => _state = state;

    /// <summary>Every persist and every issue, in the order they happened.</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>What the store holds now, which a second allocator over this store boots from.</summary>
    public InstanceIdState State => _state;

    /// <summary>How many times the allocator persisted, which is what pins the block size.</summary>
    public int PersistCount { get; private set; }

    /// <summary>The counter high-water mark of the newest persist, or 0 when nothing was persisted.</summary>
    public long PersistedCounter { get; private set; }

    /// <summary>Records an id the test just took, so it lands in the same log as the persists.</summary>
    /// <param name="id">The packed id.</param>
    public void RecordIssue(long id) => _log.Add($"issue:{InstanceIdAllocator.CounterOf(id)}");

    /// <inheritdoc />
    public InstanceIdState Read() => _state;

    /// <inheritdoc />
    public void Persist(in InstanceIdState state)
    {
        _state = state;
        PersistCount++;
        PersistedCounter = InstanceIdAllocator.CounterOf(state.PackedHighWater);
        _log.Add($"persist:{PersistedCounter}");
    }
}
