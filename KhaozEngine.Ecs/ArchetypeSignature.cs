using System;

namespace KhaozEngine.Ecs;

/// <summary>A sorted set of component-type ids identifying an archetype; value-equal by contents.</summary>
internal readonly struct ArchetypeSignature : IEquatable<ArchetypeSignature>
{
    public readonly int[] Ids;   // sorted ascending

    public ArchetypeSignature(int[] sortedIds) => Ids = sortedIds;

    public bool Equals(ArchetypeSignature other)
    {
        if (Ids.Length != other.Ids.Length) return false;
        for (int i = 0; i < Ids.Length; i++)
            if (Ids[i] != other.Ids[i]) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ArchetypeSignature s && Equals(s);

    public override int GetHashCode()
    {
        var h = new HashCode();
        for (int i = 0; i < Ids.Length; i++) h.Add(Ids[i]);
        return h.ToHashCode();
    }
}
