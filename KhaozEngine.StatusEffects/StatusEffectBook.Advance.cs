using System;

namespace KhaozEngine.StatusEffects;

public sealed partial class StatusEffectBook
{
    /// <summary>Raises due periodic events, then expires instances, in actor and application order.</summary>
    public void Advance(long now)
    {
        ValidateNow(now);
        _lastAdvancedTick = now;
        _hasAdvanced = true;

        foreach (var (actor, state) in _actors)
        {
            int index = 0;
            while (index < state.Instances.Count)
            {
                var instance = state.Instances[index];
                int period = instance.Definition.PeriodTicks;
                long limit = Math.Min(now, instance.Entry.EndTick);
                if (period > 0)
                {
                    Int128 due = (Int128)instance.Anchor + instance.NextPeriodicK * period;
                    while (due <= limit)
                    {
                        Raise(StatusEffectEventKind.Periodic, actor, instance, (long)due);
                        instance.NextPeriodicK++;
                        due = (Int128)instance.Anchor + instance.NextPeriodicK * period;
                    }
                }

                if (instance.Entry.EndTick <= now)
                    EndInstance(actor, state, index, instance.Entry.EndTick, StatusEffectEndReason.Expired);
                else
                    index++;
            }
        }
    }
}
