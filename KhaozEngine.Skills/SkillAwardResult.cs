namespace KhaozEngine.Skills;

/// <summary>What one award did: whether the child crossed a level, which parent (if any) was paid, whether
/// the parent crossed, and both totals as they now stand.</summary>
/// <remarks>
/// The two totals are here so a caller can record the award without reading the book back. A durable log
/// that stores a skill's WHOLE new total rather than a delta (which is the shape that survives a replay in
/// any order) needs exactly these two numbers, and reading them off the book afterwards is the same numbers
/// one step later, where a second award can have landed in between.
/// <para>The kernel writes nothing anywhere. A game turns this into its own events, its own presentation
/// and its own log lines.</para>
/// </remarks>
/// <param name="Crossed">Whether the child's award took it into a new level.</param>
/// <param name="Parent">The parent that was paid a share, or -1 when the child is a root or the award was
/// refused.</param>
/// <param name="ChildXp">The child's total after the award. On a refused award it is the total as it
/// stands, unchanged, because this record never lies about the book.</param>
/// <param name="ParentCrossed">Whether the parent's share took IT into a new level.</param>
/// <param name="ParentXp">The parent's total after the share, or zero when there is no parent.</param>
public readonly record struct SkillAwardResult(
    bool Crossed, int Parent, double ChildXp, bool ParentCrossed, double ParentXp)
{
    /// <summary>Whether a parent was named at all.</summary>
    public bool HasParent => Parent >= 0;

    /// <summary>The one skill a level-up presentation names for this award: the child when it crossed,
    /// else the parent when it did, else -1.</summary>
    /// <param name="skill">The child the award was paid to.</param>
    public int Presented(int skill) => Crossed ? skill : ParentCrossed ? Parent : -1;
}
