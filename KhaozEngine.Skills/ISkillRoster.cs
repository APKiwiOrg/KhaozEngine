namespace KhaozEngine.Skills;

/// <summary>Which skills exist, how they nest, and which are open. The one seam the kernel has to a game's
/// own skill identity, and the only shape of it: dense int indices from zero, and three questions.</summary>
/// <remarks>
/// A game names its skills however it likes (an enum, a content table, a generated id block) and hands the
/// kernel the INDICES. No name, no icon, no display order and no training rule crosses this boundary: the
/// display order in particular is a game's, because a storage order and a reading order are two different
/// orders and only the first one is the kernel's business.
/// <para><see cref="SkillRoster"/> is the batteries-included implementation, built through
/// <see cref="SkillRoster.Of"/>, so a game only writes its own when the roster is computed from content
/// rather than declared.</para>
/// </remarks>
public interface ISkillRoster
{
    /// <summary>How many skills there are. Valid indices are zero to <c>Count - 1</c>, and this is what a
    /// book is sized for and what a codec header declares.</summary>
    int Count { get; }

    /// <summary>Whether a skill refuses every award. A locked skill still holds and still reports whatever
    /// experience it was decoded with: locking stops it MOVING, it does not erase it.</summary>
    /// <param name="index">The skill being asked about. An index outside the roster answers locked.</param>
    bool IsLocked(int index);

    /// <summary>The parent a child's awards share up to, or -1 for a root.</summary>
    /// <remarks>The share goes exactly ONE level up. A grandparent is never paid, so a roster nested deeper
    /// than two levels is legal and only its immediate parent sees a share.</remarks>
    /// <param name="index">The skill being placed in the tree. An index outside the roster answers -1.</param>
    int ParentOf(int index);
}
