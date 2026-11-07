using System;

namespace KhaozEngine.StatusEffects;

public sealed partial class StatusEffectBook
{
    /// <summary>
    /// Settles the actor at <paramref name="now"/> before matching an instance or checking capacity.
    /// In application order, each instance raises unraised periodic ticks through min(now, EndTick),
    /// then expires at EndTick when due. These events precede the new application or refresh, so a
    /// periodic tick due now uses the previous stacks, source and magnitude. An ended window starts
    /// fresh with one stack and a new periodic anchor. A later Advance(now) does not repeat events.
    /// Swapping Apply and Advance at the same tick preserves effect entries and the event multiset
    /// when no mutation intervenes. Cross-actor event interleaving follows call order since Apply
    /// settles only its target actor. Settling runs before the definition lookup and the capacity check,
    /// so a call that returns UnknownDefinition or ActorFull, or throws because the end tick would not fit,
    /// may already have raised the same settle events that Advance(now) would raise for the actor.
    /// </summary>
    public StatusEffectApplyResult Apply(long actor, StatusEffectId effect, long now, long source = 0,
        int? durationTicks = null, int magnitude = 0)
    {
        if (actor == 0)
            throw new ArgumentOutOfRangeException(nameof(actor));
        ValidateNow(now);
        if (durationTicks is <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (_actors.TryGetValue(actor, out var state))
            SettleActor(actor, state, now);
        if (!_catalog.TryGet(effect, out var definition))
            return StatusEffectApplyResult.UnknownDefinition;

        Instance? matched = null;
        if (state is not null)
        {
            foreach (var instance in state.Instances)
            {
                var entry = instance.Entry;
                if (entry.Effect != effect ||
                    (instance.Definition.Scope == StatusEffectScope.PerSource && entry.Source != source))
                    continue;

                matched = instance;
                definition = instance.Definition;
                break;
            }
        }

        int duration = durationTicks ?? definition.DurationTicks;
        if (now > long.MaxValue - duration)
            throw new ArgumentOutOfRangeException(nameof(now), "The effect's end tick must fit in a long.");
        long endTick = now + duration;

        if (state is not null && matched is not null)
        {
            var entry = matched.Entry;
            int stacks = Math.Min(entry.Stacks + 1, definition.MaxStacks);
            bool grew = stacks > entry.Stacks;
            MarkChanged(actor, state);
            matched.Entry = new(effect, source, stacks, now, Math.Max(entry.EndTick, endTick));
            matched.Magnitude = magnitude;
            Raise(grew ? StatusEffectEventKind.Stacked : StatusEffectEventKind.Refreshed, actor, matched, now);
            return grew ? StatusEffectApplyResult.Stacked : StatusEffectApplyResult.Refreshed;
        }

        if (state is not null)
        {
            if (state.Instances.Count >= _maxEffectsPerActor)
                return StatusEffectApplyResult.ActorFull;
        }
        else
        {
            state = new ActorState();
        }

        MarkChanged(actor, state);
        var added = new Instance(definition, new(effect, source, 1, now, endTick), magnitude);
        state.Instances.Add(added);
        // Assignment preserves an existing actor's state, including an empty actor awaiting its drain.
        _actors[actor] = state;
        Raise(StatusEffectEventKind.Applied, actor, added, now);
        return StatusEffectApplyResult.Applied;
    }
}
