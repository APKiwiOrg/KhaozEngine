using System.Collections.Generic;

namespace KhaozEngine.Movement;

/// <summary>The inputs a bake must have been made from: capture options, labelled source digests and the
/// profiles in any order. Loading compares a stored bake with the identity encoded from this expectation.</summary>
public sealed record NavBakeExpectation(PhysicsNavBakeOptions Options, NavBakeSources Sources,
    IReadOnlyList<NavBakeProfile> Profiles);
