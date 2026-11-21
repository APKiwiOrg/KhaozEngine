namespace KhaozEngine.Locomotion;

/// <summary>Which evidence answers the water boundary question for the leases of one environment context. It is
/// engine-owned and set where a context is created. Games never choose it. It is one byte, so every lease holds it in
/// the padding beside its flags and the scope adds nothing to a lease's allocation.</summary>
internal enum MovementWaterBoundaryScope : byte
{
    /// <summary>Runtime movement. The capture certificate may answer, and a declined question takes the full
    /// coverage solve.</summary>
    Runtime = 0,
    /// <summary>Navigation bake proofs. Every question takes the full coverage solve. A refusal of that solve rejects
    /// a Drop link, refuses a terrain Drop or refuses the whole profile bake, so a certificate answer there could
    /// change published navigation. Bakes keep their links, refusals and exceptions bit for bit, while runtime
    /// movement, runtime Drop following included, may continue where a bake refused.</summary>
    NavigationBake = 1,
}
