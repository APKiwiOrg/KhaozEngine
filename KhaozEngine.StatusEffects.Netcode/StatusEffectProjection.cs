using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>Single-threaded bridge from book changes to immutable replicated effect windows.</summary>
public sealed class StatusEffectProjection
{
    private readonly StatusEffectBook _book;
    private readonly IStatusEffectProjectionTarget _target;
    private readonly SortedSet<long> _pending = new();
    private readonly List<long> _actors = new();
    private readonly List<StatusEffectEntry> _entries = new();

    public StatusEffectProjection(StatusEffectBook book, IStatusEffectProjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(target);
        _book = book;
        _target = target;
    }

    /// <summary>Publishes changed and deferred actors in ascending order, forgetting only gone actors.</summary>
    public void Publish()
    {
        _actors.Clear();
        _book.DrainChangedActors(_actors);
        _pending.UnionWith(_actors);
        // Snapshot the sorted work set so publication can remove completed actors while iterating.
        _actors.Clear();
        _actors.AddRange(_pending);
        foreach (long actor in _actors)
        {
            _entries.Clear();
            _book.CopyEntries(actor, _entries, shownOnly: true);
            // The book caps every actor at 255 entries. Copy before reusing the scratch list next actor or tick.
            var state = new ActiveStatusEffects { Entries = _entries.ToImmutableArray() };
            switch (_target.Publish(actor, in state))
            {
                case StatusEffectPublishResult.Published:
                    _pending.Remove(actor);
                    break;
                case StatusEffectPublishResult.Deferred:
                    break;
                case StatusEffectPublishResult.Gone:
                    _book.Forget(actor);
                    _pending.Remove(actor);
                    break;
                default:
                    throw new InvalidOperationException("Unknown status effect publication result.");
            }
        }
    }
}
