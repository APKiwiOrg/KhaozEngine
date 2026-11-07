namespace KhaozEngine.NetWorld;

/// <summary>Rules that refuse to rewrite a stored cell snapshot body.</summary>
public enum CellBlobMigrationRefusal
{
    /// <summary>Refuses a body that does not walk structurally at its stated wire generation.</summary>
    KnownGenerationUnwalkable,
    /// <summary>Refuses a body for which no candidate wire generation produces a valid walk.</summary>
    NoInferredGeneration,
}
