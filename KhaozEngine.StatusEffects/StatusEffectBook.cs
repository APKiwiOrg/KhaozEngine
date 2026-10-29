using System;
using System.Collections.Generic;

namespace KhaozEngine.StatusEffects;

/// <summary>
/// Single-threaded authority for transient timed effects. Drain notifications after a step before
/// responding with further changes. The book never invokes gameplay callbacks.
/// The catalog must keep each id's definition the same for this book's lifetime. Each instance
/// retains its captured definition for scope matching, reapplication, periodic events and modifiers,
/// even if a custom catalog later returns a different definition object.
/// </summary>
public sealed partial class StatusEffectBook
{
    private readonly IStatusEffectCatalog _catalog;
    private readonly int _maxEffectsPerActor;
    private readonly SortedDictionary<long, ActorState> _actors = new();
    private readonly SortedSet<long> _changedActors = new();
    private readonly List<StatusEffectEvent> _events = new();
    private long _lastAdvancedTick;
    private bool _hasAdvanced;
    private ulong _modifierStamp;

    public StatusEffectBook(IStatusEffectCatalog catalog, int maxEffectsPerActor = 32)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (maxEffectsPerActor is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(maxEffectsPerActor));
        _catalog = catalog;
        _maxEffectsPerActor = maxEffectsPerActor;
    }

    /// <summary>Retained actors, including empty actors awaiting a changed-actor drain.</summary>
    public int ActorCount => _actors.Count;

    public bool Has(long actor, StatusEffectId effect)
    {
        if (!_actors.TryGetValue(actor, out var state))
            return false;
        foreach (var instance in state.Instances)
            if (instance.Entry.Effect == effect)
                return true;
        return false;
    }

    /// <summary>Appends entries in application order and returns the number appended.</summary>
    public int CopyEntries(long actor, List<StatusEffectEntry> into, bool shownOnly)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!_actors.TryGetValue(actor, out var state))
            return 0;
        int count = 0;
        foreach (var instance in state.Instances)
        {
            if (shownOnly && !instance.Definition.Shown)
                continue;
            into.Add(instance.Entry);
            count++;
        }
        return count;
    }

    /// <summary>Returns the exact channel sum when it fits in an int, including cancelling contributions.</summary>
    /// <exception cref="OverflowException">The final sum is outside the int range.</exception>
    public int ModifierTotal(long actor, int channel)
    {
        if (!_actors.TryGetValue(actor, out var state))
            return 0;
        Int128 total = 0;
        foreach (var instance in state.Instances)
            foreach (var modifier in instance.Definition.Modifiers)
                if (modifier.Channel == channel)
                    total += (Int128)modifier.Value * instance.Entry.Stacks;
        return checked((int)total);
    }

    /// <summary>
    /// The actor's last book-wide change stamp, or zero when no instances remain. Stamps never wrap.
    /// </summary>
    public ulong ModifierVersion(long actor) =>
        _actors.TryGetValue(actor, out var state) && state.Instances.Count != 0 ? state.Version : 0;

    /// <summary>Appends queued events in order and consumes them.</summary>
    public void DrainEvents(List<StatusEffectEvent> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        into.AddRange(_events);
        _events.Clear();
    }

    /// <summary>Appends changed actors in ascending order, then drops any that remain empty.</summary>
    public void DrainChangedActors(List<long> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (long actor in _changedActors)
        {
            into.Add(actor);
            if (_actors.TryGetValue(actor, out var state) && state.Instances.Count == 0)
                _actors.Remove(actor);
        }
        _changedActors.Clear();
    }

    /// <summary>Drops an actor and its pending change notification without adding lifecycle events.</summary>
    public void Forget(long actor)
    {
        _actors.Remove(actor);
        _changedActors.Remove(actor);
    }

    private void MarkChanged(long actor, ActorState state)
    {
        // Checked before the mutation so a cached stamp can never alias an older state.
        _modifierStamp = checked(_modifierStamp + 1);
        state.Version = _modifierStamp;
        _changedActors.Add(actor);
    }

    private void ValidateNow(long now)
    {
        if (_hasAdvanced && now < _lastAdvancedTick)
            throw new ArgumentOutOfRangeException(nameof(now), "Time must not precede the last advance.");
    }

    private void Raise(StatusEffectEventKind kind, long actor, Instance instance, long tick,
        StatusEffectEndReason reason = StatusEffectEndReason.None)
    {
        var entry = instance.Entry;
        _events.Add(new(kind, actor, entry.Effect, entry.Source, entry.Stacks, instance.Magnitude, tick, reason));
    }

    private sealed class ActorState
    {
        public readonly List<Instance> Instances = new();
        public ulong Version;
    }

    private sealed class Instance(StatusEffectDefinition definition, StatusEffectEntry entry, int magnitude)
    {
        public readonly StatusEffectDefinition Definition = definition;
        public StatusEffectEntry Entry = entry;
        public int Magnitude = magnitude;
        public readonly long Anchor = entry.StartTick;
        // Wider arithmetic keeps a due tick just beyond long.MaxValue from wrapping into the past.
        public Int128 NextPeriodicK = 1;
    }
}
