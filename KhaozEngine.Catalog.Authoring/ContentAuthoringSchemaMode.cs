namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What <see cref="IContentAuthoringStore.InitializeAsync"/> is allowed to do to the database it opens. The
/// two modes are the journal's (<c>SqliteJournalSchema.cs:9-13</c>), which is the style this store follows
/// because its schema will gain tables as new content types land and as inheritance ships, so it needs a
/// migration path from its first release.
/// </summary>
public enum ContentAuthoringSchemaMode
{
    /// <summary>Creates the schema when the database is empty, then validates it.</summary>
    AutoCreate,

    /// <summary>
    /// Validates and creates nothing. An empty or mismatched database is REFUSED with a
    /// <see cref="ContentAuthoringException"/> naming the object and the required migration.
    /// <para>
    /// This is what a production host sets, so a typo in a connection string cannot silently create a
    /// second empty catalog and boot a world with no content in it.
    /// </para>
    /// </summary>
    ValidateOnly,
}
