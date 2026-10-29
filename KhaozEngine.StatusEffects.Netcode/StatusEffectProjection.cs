using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.StatusEffects.Netcode;

/// <summary>Single-threaded bridge from book changes to immutable replicated effect windows.</summary>
public sealed class StatusEffectProjection
{
    private readonly StatusEffectBook _book;
    private readonly IStatusEffectProjectionTarget _target;
    private readonly List<long> _actors = new();
    private readonly List<StatusEffectEntry> _entries = new();

    public StatusEffectProjection(StatusEffectBook book, IStatusEffectProjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(target);
        _book = book;
        _target = target;
    }

    /// <summary>Drains changed actors after Advance and gameplay applies, forgetting failed publishes.</summary>
    public void Publish()
    {
        _actors.Clear();
        _book.DrainChangedActors(_actors);
        foreach (long actor in _actors)
        {
            _entries.Clear();
            _book.CopyEntries(actor, _entries, shownOnly: true);
            // The book caps every actor at 255 entries. Copy before reusing the scratch list next actor or tick.
            var state = new ActiveStatusEffects { Entries = _entries.ToImmutableArray() };
            if (!_target.TryPublish(actor, in state))
                _book.Forget(actor);
        }
    }
}
