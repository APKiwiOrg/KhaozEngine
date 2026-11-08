namespace KhaozEngine.Locomotion;

/// <summary>Single-use bookkeeping provisioned with the context workspace, never a per-read allocation.</summary>
internal struct MovementColdPlacementUse
{
    long version;
    bool available;

    internal long Issue()
    {
        version = checked(version + 1);
        available = true;
        return version;
    }

    internal bool TryConsume(long candidate)
    {
        if (!available || candidate != version) return false;
        available = false;
        return true;
    }

    internal void Clear() => available = false;
}
