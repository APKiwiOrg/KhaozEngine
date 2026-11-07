namespace KhaozEngine.StatusEffects;

public sealed partial class StatusEffectBook
{
    /// <summary>
    /// Removes matching instances. A null source matches all sources. Removal events use the last
    /// advanced tick, or zero before the first advance.
    /// </summary>
    public bool Remove(long actor, StatusEffectId effect, long? source = null)
    {
        if (!_actors.TryGetValue(actor, out var state))
            return false;
        bool removed = false;
        int index = 0;
        while (index < state.Instances.Count)
        {
            var entry = state.Instances[index].Entry;
            if (entry.Effect == effect && (source is null || entry.Source == source.Value))
            {
                EndInstance(actor, state, index, _lastAdvancedTick, StatusEffectEndReason.Removed);
                removed = true;
            }
            else
                index++;
        }
        return removed;
    }

    /// <summary>Ends every instance at the last advanced tick, or zero before the first advance.</summary>
    public int Clear(long actor)
    {
        if (!_actors.TryGetValue(actor, out var state))
            return 0;
        int count = state.Instances.Count;
        while (state.Instances.Count != 0)
            EndInstance(actor, state, 0, _lastAdvancedTick, StatusEffectEndReason.Cleared);
        return count;
    }

    private void EndInstance(long actor, ActorState state, int index, long tick, StatusEffectEndReason reason)
    {
        MarkChanged(actor, state);
        Raise(StatusEffectEventKind.Ended, actor, state.Instances[index], tick, reason);
        state.Instances.RemoveAt(index);
    }
}
