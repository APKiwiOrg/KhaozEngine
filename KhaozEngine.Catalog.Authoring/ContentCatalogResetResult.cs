using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What one catalog RESET destroyed, and the schema version and store epoch the recreated schema came back
/// with. Every provider's reset answers with this, so an operator console prints one line whichever backend
/// it drove.
/// <para>
/// <b>The hashes are the ones that STOOD, which is the only moment they can be read.</b> A reset drops
/// <c>catalog_version</c> with everything else, so after it returns there is no query that can answer what
/// the store used to serve. A caller replacing content at the same version number needs exactly those two
/// hashes to tell whether a pack root on disk is the old content or the new, and this record is the last
/// place they exist.
/// </para>
/// <para>
/// <b><see cref="StoreEpoch"/> is NEW, and so is <see cref="SchemaVersion"/>.</b> The reset recreates the
/// schema through the same script the initializer runs, which mints a fresh epoch and writes the schema
/// version this build writes. A catalog that stood at an OLDER schema version therefore comes back at the
/// build's own, which <see cref="PriorSchemaVersion"/> and <see cref="SchemaVersion"/> both say. A catalog
/// at a NEWER one is refused before anything is dropped, so no result can report one.
/// </para>
/// <para>
/// <b>The record CANNOT be built into a state that says two things.</b> Every rule runs in the constructor,
/// and every property is get-only, so a <c>with</c> expression cannot move one value past the rules the
/// others were checked against. The rules refuse manifest hashes under version 0, a hash without its pair,
/// a version row's hashes beside no version dropped, a prior schema version newer than the recreated one,
/// and any claim about what stood on a reset that did not read it.
/// </para>
/// </summary>
/// <param name="ActiveVersion">The store's ACTIVE version before the reset, <c>catalog_metadata.active_version</c>, or 0 when it had published nothing or nothing was read. A pin is not reported: a boot serves <c>pinned_version</c> when one is set, and this is not that.</param>
/// <param name="ServerManifestHash">The active version's server manifest hash, or null when its row was not found or nothing was read.</param>
/// <param name="ClientManifestHash">The active version's client manifest hash, or null when its row was not found or nothing was read.</param>
/// <param name="VersionsDropped">How many rows <c>catalog_version</c> held, or 0 when nothing was read.</param>
/// <param name="RowsDropped">How many rows <c>catalog_row</c> held, every revision counted, or 0 when nothing was read.</param>
/// <param name="StoreEpoch">The epoch the recreated schema minted, which no earlier version shares.</param>
/// <param name="PriorSchemaVersion">The schema version the catalog that was read stood at, or 0 when nothing was read.</param>
/// <param name="SchemaVersion">The schema version the recreated catalog carries.</param>
/// <param name="PriorState">Whether a whole catalog stood and was read, none stood at all, or a partial one or one with no readable schema version stood and could not be read.</param>
/// <exception cref="ArgumentOutOfRangeException">A count or a version number is out of range, or the state is not one of the declared values.</exception>
/// <exception cref="ArgumentException">The epoch is empty, or the arguments contradict each other.</exception>
public sealed record ContentCatalogResetResult(
    int ActiveVersion,
    string? ServerManifestHash,
    string? ClientManifestHash,
    int VersionsDropped,
    int RowsDropped,
    string StoreEpoch,
    int PriorSchemaVersion,
    int SchemaVersion,
    ContentCatalogPriorState PriorState)
{
    /// <summary>The store's active version before the reset, or 0. Never the pinned version a boot serves when one is set.</summary>
    public int ActiveVersion { get; } = ActiveVersion;

    /// <summary>The active version's server manifest hash, or null.</summary>
    public string? ServerManifestHash { get; } = ServerManifestHash;

    /// <summary>The active version's client manifest hash, or null.</summary>
    public string? ClientManifestHash { get; } = ClientManifestHash;

    /// <summary>How many rows <c>catalog_version</c> held.</summary>
    public int VersionsDropped { get; } = VersionsDropped;

    /// <summary>How many rows <c>catalog_row</c> held, every revision counted.</summary>
    public int RowsDropped { get; } = RowsDropped;

    /// <summary>The epoch the recreated schema minted.</summary>
    public string StoreEpoch { get; } = StoreEpoch;

    /// <summary>The schema version the catalog that was read stood at, or 0 when nothing was read.</summary>
    public int PriorSchemaVersion { get; } = PriorSchemaVersion;

    /// <summary>The schema version the recreated catalog carries.</summary>
    public int SchemaVersion { get; } = SchemaVersion;

    /// <summary>
    /// Whether a whole catalog stood and was read, none stood at all, or a partial one or one with no readable
    /// schema version stood and could not be read. Declared LAST so the CONSISTENCY RULES run on the way in,
    /// with every other value in scope.
    /// </summary>
    public ContentCatalogPriorState PriorState { get; } = Validate(
        ActiveVersion,
        ServerManifestHash,
        ClientManifestHash,
        VersionsDropped,
        RowsDropped,
        StoreEpoch,
        PriorSchemaVersion,
        SchemaVersion,
        PriorState);

    /// <summary>
    /// The one line an operator reads, and the same text the reset files in the new store's audit, so the
    /// console output and the stored record cannot say two different things. It is a fixed sentence around
    /// two 64-character hashes, a 32-character epoch and five numbers, well under the 4096 characters
    /// <c>catalog_audit.before_value</c> accepts.
    /// </summary>
    public string Summary => PriorState switch
    {
        ContentCatalogPriorState.Read => FormattableString.Invariant(
            $"Catalog reset. It stood at {Stood} on schema version {PriorSchemaVersion}, and {VersionsDropped} versions and {RowsDropped} row revisions were dropped. {Now}"),
        ContentCatalogPriorState.Absent => FormattableString.Invariant(
            $"Catalog reset. It stood at no catalog at all, because the database carried none of its tables, so nothing was dropped. {Now}"),
        _ => FormattableString.Invariant(
            $"Catalog reset. It stood at a PARTIAL catalog or one with no readable schema version, which no read could describe, so what stood was dropped and nothing can be said about what it held. {Now}"),
    };

    /// <summary>What the recreate left, which every branch ends on.</summary>
    string Now => FormattableString.Invariant(
        $"The new store is at schema version {SchemaVersion} with store epoch {StoreEpoch}.");

    /// <summary>
    /// Every rule that keeps the record from saying two things, returning the state it was handed so it can
    /// sit in an initializer and run exactly once.
    /// </summary>
    static ContentCatalogPriorState Validate(
        int activeVersion,
        string? serverManifestHash,
        string? clientManifestHash,
        int versionsDropped,
        int rowsDropped,
        string storeEpoch,
        int priorSchemaVersion,
        int schemaVersion,
        ContentCatalogPriorState priorState)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(versionsDropped);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsDropped);
        ArgumentOutOfRangeException.ThrowIfNegative(priorSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
        ArgumentException.ThrowIfNullOrEmpty(storeEpoch);
        if (!Enum.IsDefined(priorState))
        {
            throw new ArgumentOutOfRangeException(nameof(priorState), priorState, "Not a declared prior state.");
        }

        // A version row carries both hashes or it was not found at all, so one without the other would be a
        // half-read nobody can act on.
        if (serverManifestHash is null != (clientManifestHash is null))
        {
            throw new ArgumentException(
                "A catalog version row carries both manifest hashes or neither, so a reset result cannot hold one of them.",
                nameof(serverManifestHash));
        }

        // The failure this rule exists for: a store whose active_version names a row catalog_version does not
        // hold used to come back as the number 2 beside the words "nothing published", which are not the same
        // answer. The number now keeps its meaning and the text says the row was missing.
        if (activeVersion == 0 && serverManifestHash is not null)
        {
            throw new ArgumentException(
                "A store that published nothing has no active version's manifest hashes, so a reset result cannot carry them under version 0.",
                nameof(activeVersion));
        }

        // A version row that was found is a row catalog_version held, so the reset dropped at least that one.
        // The converse is not a rule: a file with broken foreign keys can hold rows and no version at all.
        if (serverManifestHash is not null && versionsDropped == 0)
        {
            throw new ArgumentException(
                "A reset result carrying a version row's manifest hashes dropped at least that version, so it cannot report no versions dropped.",
                nameof(versionsDropped));
        }

        if (priorState == ContentCatalogPriorState.Read)
        {
            // A catalog that was read stood at a real schema version, and never a newer one than the build
            // recreated: the reset refuses a newer one before it drops anything.
            if (priorSchemaVersion < 1 || priorSchemaVersion > schemaVersion)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(priorSchemaVersion),
                    priorSchemaVersion,
                    "A catalog that was read stood at a schema version from 1 up to the one the reset recreated.");
            }
        }
        else if (activeVersion != 0
            || serverManifestHash is not null
            || versionsDropped != 0
            || rowsDropped != 0
            || priorSchemaVersion != 0)
        {
            throw new ArgumentException(
                "A reset that read no prior catalog cannot report a version, a manifest hash, a schema version or anything dropped.",
                nameof(priorState));
        }

        return priorState;
    }

    /// <summary>
    /// The store's ACTIVE version, named by number and by both hashes when it had one. The words say "active
    /// version" because that is what was read, and a store pinned below it served the pin instead.
    /// </summary>
    string Stood => this switch
    {
        { ActiveVersion: 0 } => "nothing published",
        { ServerManifestHash: null } => FormattableString.Invariant(
            $"active version {ActiveVersion}, whose version row was MISSING"),
        _ => FormattableString.Invariant(
            $"active version {ActiveVersion}, server manifest {ServerManifestHash}, client manifest {ClientManifestHash}"),
    };
}
