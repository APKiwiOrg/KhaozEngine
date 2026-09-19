using System;

namespace KhaozEngine.Skills;

/// <summary>Declares a <see cref="SkillRoster"/>: how many skills, which are locked, and which nest under
/// which.</summary>
/// <remarks>
/// Every skill starts OPEN and a root, which is the shape a game with no tree wants and the one that needs
/// no calls at all. A game whose roster is mostly aspiration inverts that with <see cref="LockAll"/> and
/// then names the live ones, so a skill added to its enum and forgotten is inert rather than silently
/// trainable.
/// <para>The builder holds its own arrays and <see cref="Build"/> copies them, so a builder reused after a
/// build cannot reach back into the roster it already handed out.</para>
/// </remarks>
public sealed class SkillRosterBuilder
{
    readonly bool[] _locked;
    readonly int[] _parent;

    internal SkillRosterBuilder(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        _locked = new bool[count];
        _parent = new int[count];
        Array.Fill(_parent, -1);
    }

    /// <summary>How many skills this roster will hold.</summary>
    public int Count => _locked.Length;

    /// <summary>Locks every skill, for a roster whose live set is the exception rather than the rule. Call
    /// it first, then <see cref="Unlock"/> the ones a system actually pays.</summary>
    public SkillRosterBuilder LockAll()
    {
        Array.Fill(_locked, true);
        return this;
    }

    /// <summary>Locks one skill, which refuses every award until a later roster opens it.</summary>
    /// <param name="index">The skill being locked.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the roster.</exception>
    public SkillRosterBuilder Lock(int index)
    {
        _locked[Checked(index, nameof(index))] = true;
        return this;
    }

    /// <summary>Opens one skill.</summary>
    /// <param name="index">The skill being opened.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the roster.</exception>
    public SkillRosterBuilder Unlock(int index)
    {
        _locked[Checked(index, nameof(index))] = false;
        return this;
    }

    /// <summary>Nests one skill under another, which is what makes an award to the child pay the parent a
    /// share. Calling it twice for the same child keeps the last parent.</summary>
    /// <param name="child">The skill being placed.</param>
    /// <param name="parent">The skill paid a share of the child's awards, or -1 to make the child a
    /// root.</param>
    /// <exception cref="ArgumentOutOfRangeException">Either index is outside the roster.</exception>
    /// <exception cref="ArgumentException"><paramref name="parent"/> is <paramref name="child"/>.</exception>
    public SkillRosterBuilder Parent(int child, int parent)
    {
        int at = Checked(child, nameof(child));
        if (parent != -1) Checked(parent, nameof(parent));
        if (parent == child) throw new ArgumentException("a skill cannot be its own parent", nameof(parent));
        _parent[at] = parent;
        return this;
    }

    /// <summary>Freezes the declaration into a roster.</summary>
    /// <exception cref="InvalidOperationException">A parent chain loops back on itself.</exception>
    public SkillRoster Build()
    {
        // A loop is caught HERE rather than at the first award, because an award walks one step up and
        // would never notice: a two-skill cycle pays each other a share forever without recursing, and the
        // only visible symptom is two skills nobody can explain the level of.
        for (int start = 0; start < _parent.Length; start++)
        {
            int at = _parent[start];
            for (int step = 0; step < _parent.Length && at >= 0; step++) at = _parent[at];
            if (at >= 0)
                throw new InvalidOperationException(
                    $"the parent chain from skill {start} loops rather than reaching a root");
        }
        return new SkillRoster((bool[])_locked.Clone(), (int[])_parent.Clone());
    }

    int Checked(int index, string name)
    {
        if (index < 0 || index >= _locked.Length)
            throw new ArgumentOutOfRangeException(name, index,
                $"the roster holds {_locked.Length} skills, so an index is 0 to {_locked.Length - 1}");
        return index;
    }
}
