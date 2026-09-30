namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What <see cref="IContentAuthoringStore.InitializeAsync"/> may change when opening a catalog.
/// The mode governs initialization, not subsequent operations on the store.
/// </summary>
public enum ContentAuthoringSchemaMode
{
    /// <summary>Creates or migrates the schema, validates it, then synchronizes type registrations.</summary>
    AutoCreate = 0,

    /// <summary>
    /// Validates without creating or migrating the schema, then synchronizes type registrations.
    /// Synchronization inserts new types and refreshes their settings and update times when changed.
    /// An empty or mismatched database is refused with a
    /// <see cref="ContentAuthoringException"/> naming the object and the required migration.
    /// <para>
    /// This is what a production host sets, so a typo in a connection string cannot silently create a
    /// second empty catalog and boot a world with no content in it.
    /// </para>
    /// </summary>
    ValidateOnly = 1,

    /// <summary>
    /// Validates the existing schema and checks registered type id/key pairings with reads only.
    /// Creates and migrates nothing, leaves type settings and row times unchanged, and accepts registry
    /// additions and changes to nonidentity settings. Conflicting stored id/key pairings are refused.
    /// <para>
    /// Bundle exports and upgrade previews use stored type settings with this build's field schemas.
    /// This mode does not restrict later writes. ReadPublishBaselineAsync can clear a stale draft freeze.
    /// </para>
    /// </summary>
    ValidateOnlyWithoutTypeSync = 2,
}
