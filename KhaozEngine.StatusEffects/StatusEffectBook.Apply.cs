using System;

namespace KhaozEngine.StatusEffects;

public sealed partial class StatusEffectBook
{
    public StatusEffectApplyResult Apply(long actor, StatusEffectId effect, long now, long source = 0,
        int? durationTicks = null, int magnitude = 0)
    {
        if (actor == 0)
            throw new ArgumentOutOfRangeException(nameof(actor));
        ValidateNow(now);
        if (durationTicks is <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationTicks));
        if (!_catalog.TryGet(effect, out var definition))
            return StatusEffectApplyResult.UnknownDefinition;

        int duration = durationTicks ?? definition.DurationTicks;
        if (now > long.MaxValue - duration)
            throw new ArgumentOutOfRangeException(nameof(now), "The effect's end tick must fit in a long.");
        long endTick = now + duration;

        if (_actors.TryGetValue(actor, out var state))
        {
            foreach (var instance in state.Instances)
            {
                var entry = instance.Entry;
                if (entry.Effect != effect ||
                    (definition.Scope == StatusEffectScope.PerSource && entry.Source != source))
                    continue;

                int stacks = Math.Min(entry.Stacks + 1, definition.MaxStacks);
                bool grew = stacks > entry.Stacks;
                MarkChanged(actor, state);
                instance.Entry = new(effect, source, stacks, now, Math.Max(entry.EndTick, endTick));
                instance.Magnitude = magnitude;
                Raise(grew ? StatusEffectEventKind.Stacked : StatusEffectEventKind.Refreshed, actor, instance, now);
                return grew ? StatusEffectApplyResult.Stacked : StatusEffectApplyResult.Refreshed;
            }
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
