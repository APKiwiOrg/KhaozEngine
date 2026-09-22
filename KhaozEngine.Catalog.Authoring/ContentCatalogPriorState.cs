namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What a catalog RESET found in the database before it wrote anything, which is what decides whether the
/// rest of <see cref="ContentCatalogResetResult"/> means anything at all.
/// <para>
/// The three cases are genuinely different actions and an operator is entitled to know which one happened. A
/// reset that REPLACED a catalog can say what it destroyed. A reset that CREATED one destroyed nothing. A
/// reset that REPAIRED a partial one destroyed something and cannot say what.
/// </para>
/// </summary>
public enum ContentCatalogPriorState
{
    /// <summary>
    /// A whole catalog stood and was read, at the build's own schema version or an older one. The version,
    /// the hashes, the dropped counts and the prior schema version on the result are what it held.
    /// </summary>
    Read = 0,

    /// <summary>
    /// The database carried no catalog table at all, so the reset CREATED the schema and dropped nothing.
    /// The scripted release path is reset then import, and a first release against a new database takes this
    /// branch rather than being refused for a migration that exists only as this script.
    /// </summary>
    Absent = 1,

    /// <summary>
    /// The database carried SOME of the catalog's tables and not the whole set its schema version declares,
    /// which no store can open and no read can describe. A forced reset drops what stands and recreates the
    /// schema, which repairs it, and says so here because there is nothing truthful it can put in the version,
    /// the hashes and the counts.
    /// </summary>
    Unreadable = 2,
}
