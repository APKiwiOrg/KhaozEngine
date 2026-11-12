namespace KhaozEngine.Ecs;

public sealed partial class World
{
    // Counts include dead entity versions hidden by Added/Changed enumeration. Value copies only.
    internal (int Added, int AddedOrder, int Changed, int ChangedOrder, int Removed) ChangeTrackingCountsForTests
    {
        get
        {
            int removed = 0;
            foreach (var entries in _removed.Values) removed += entries.Count;
            return (_added.Count, _addedOrder.Count, _changed.Count, _changedOrder.Count, removed);
        }
    }
}
