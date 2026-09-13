namespace KhaozEngine.Terrain;

/// <summary>The individual and merged representations one retained prop cluster draws for the current focus.</summary>
public readonly record struct PropClusterDrawState(
    bool DrawsIndividuals,
    float IndividualDissolveFloor,
    bool DrawsMerged,
    float MergedDissolve,
    bool MergedComplement,
    long IndividualSourceGeneration,
    long MergedSourceGeneration);
