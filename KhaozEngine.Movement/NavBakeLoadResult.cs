namespace KhaozEngine.Movement;

/// <summary>Outcome of <see cref="GroundNavigationBake.Load(System.IO.Stream, NavBakeExpectation)"/>.
/// <see cref="NavBakeLoadStatus.Loaded"/> carries the bake and an empty detail. Every other status carries a null
/// bake and developer text naming the first failing check. Detail is never shown to players.</summary>
public sealed record NavBakeLoadResult(NavBakeLoadStatus Status, string Detail, GroundNavigationBake? Bake);
