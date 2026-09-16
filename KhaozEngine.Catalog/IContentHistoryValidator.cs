using System.Collections.Generic;

namespace KhaozEngine.Catalog;

/// <summary>
/// A content type's own checks that need the PREVIOUS published snapshot, which
/// <see cref="IContentValidator"/> carries no way to hand over. A validator implementing this is reached
/// through the extra overload by pass 6 of <see cref="ContentValidator"/>, the Instances band, and gets the
/// same <c>previous</c> and the same rule set the run itself was given.
/// <para>
/// <b>The plain seam is what makes a change-shaped check unreachable, so the change-shaped checks get a
/// seam of their own.</b> A publish is the one caller that holds a previous version, and without this a
/// validator reached from a publish sees the same null a boot does, which silently turns every
/// publish-only check off (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/962">962</see>).
/// </para>
/// <para>
/// It is ADDITIVE. The base <see cref="IContentValidator.Validate"/> stays the seam a game registers
/// against, pass 6 falls back to it for a band registration that does not implement this, and the game band
/// never reaches this overload at all: a game validator is untrusted code and the previous snapshot is not
/// something the engine hands it.
/// </para>
/// <para>
/// An implementation is PURE, exactly as the base seam is: no side effects, no logging, no counters, no
/// ambient reads and no throwing for a content reason. It ACCUMULATES rather than stopping at the first
/// defect.
/// </para>
/// </summary>
public interface IContentHistoryValidator : IContentValidator
{
    /// <summary>
    /// Adds a finding for every defect this type's rules see in the candidate, INCLUDING the ones that can
    /// only be seen against the version the candidate is based on.
    /// </summary>
    /// <param name="candidate">The complete candidate, which a publish builds and a boot decodes.</param>
    /// <param name="previous">
    /// The snapshot of the version the candidate is based on, or null. It is null at boot, null for a first
    /// publish and null in almost every test, and a check that needs it is SKIPPED when it is, which is a
    /// property of the argument rather than a mode flag.
    /// </param>
    /// <param name="rules">The full ordered remap rule set the run was handed, contracts 8.1.</param>
    /// <param name="findings">The accumulating sink, which an implementation only ever adds to.</param>
    void Validate(
        IContentSnapshot candidate,
        IContentSnapshot? previous,
        IReadOnlyList<RemapRule> rules,
        ICollection<ContentFinding> findings);
}
