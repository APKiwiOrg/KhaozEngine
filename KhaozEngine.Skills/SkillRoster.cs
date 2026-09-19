using System;

namespace KhaozEngine.Skills;

/// <summary>The batteries-included <see cref="ISkillRoster"/>: two small arrays, built once through
/// <see cref="Of"/> and immutable afterwards.</summary>
/// <remarks>Immutable on purpose. A book, a codec and an award path all read the roster, and a roster that
/// could be relocked underneath them would make an award legal where it was asked and illegal where it was
/// written. A game that really does open a skill at runtime builds a new roster and hands it to the next
/// book it decodes.</remarks>
public sealed class SkillRoster : ISkillRoster
{
    readonly bool[] _locked;
    readonly int[] _parent;

    internal SkillRoster(bool[] locked, int[] parent)
    {
        _locked = locked;
        _parent = parent;
    }

    /// <summary>Starts a builder for a roster of a given size. Everything is OPEN and a root until told
    /// otherwise, so the smallest useful roster is one call: <c>SkillRoster.Of(n).Build()</c>.</summary>
    /// <param name="count">How many skills the roster holds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero or negative.</exception>
    public static SkillRosterBuilder Of(int count) => new(count);

    /// <summary>A roster of open, parentless skills, which is what a game with no tree and no locking
    /// wants.</summary>
    /// <param name="count">How many skills the roster holds.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is zero or negative.</exception>
    public static SkillRoster Flat(int count) => Of(count).Build();

    /// <inheritdoc/>
    public int Count => _locked.Length;

    /// <inheritdoc/>
    public bool IsLocked(int index) => index < 0 || index >= _locked.Length || _locked[index];

    /// <inheritdoc/>
    public int ParentOf(int index) => index >= 0 && index < _parent.Length ? _parent[index] : -1;

    /// <summary>Whether a skill has children, which is what a display walk asks and what says a skill is
    /// paid a share rather than only earning one.</summary>
    /// <param name="index">The skill being asked about.</param>
    public bool IsParent(int index)
    {
        // Guarded rather than left to the scan: -1 is the stored value for a root, so an unguarded scan
        // would answer that -1 is the parent of every root in the roster.
        if (index < 0 || index >= _locked.Length) return false;
        for (int i = 0; i < _parent.Length; i++)
            if (_parent[i] == index) return true;
        return false;
    }
}
