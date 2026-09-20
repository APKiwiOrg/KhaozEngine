using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What one catalog RESET destroyed, and the store epoch the recreated schema minted in its place. Every
/// provider's reset answers with this, so an operator console prints one line whichever backend it drove.
/// <para>
/// <b>The hashes are the ones that STOOD, which is the only moment they can be read.</b> A reset drops
/// <c>catalog_version</c> with everything else, so after it returns there is no query that can answer what
/// the store used to serve. A caller replacing content at the same version number needs exactly those two
/// hashes to tell whether a pack root on disk is the old content or the new, and this record is the last
/// place they exist.
/// </para>
/// <para>
/// <b><see cref="StoreEpoch"/> is NEW.</b> The reset recreates the schema through the same script the
/// initializer runs, and that script mints a fresh epoch. A reset store is a new store: it shares no history
/// with the one it replaced, so a durable page stamped against the old epoch must not be taken for a page of
/// this one.
/// </para>
/// <para>
/// <b>The record CANNOT be built into a state that says two things.</b> The constructor refuses a version
/// number with no hashes behind it being read as nothing published, a hash without its pair, and any claim
/// about what stood on a reset that did not read one. A console reading this record is reading facts that
/// agree with each other, which is the point of the number and the text living in one type.
/// </para>
/// </summary>
/// <param name="ActiveVersion">The version the store served before the reset, or 0 when it had published nothing.</param>
/// <param name="ServerManifestHash">The active version's server manifest hash, or null when its row was not found.</param>
/// <param name="ClientManifestHash">The active version's client manifest hash, or null when its row was not found.</param>
/// <param name="VersionsDropped">How many rows <c>catalog_version</c> held.</param>
/// <param name="RowsDropped">How many rows <c>catalog_row</c> held, every revision counted.</param>
/// <param name="StoreEpoch">The epoch the recreated schema minted, which no earlier version shares.</param>
/// <param name="PriorState">Whether a whole catalog stood and was read, none stood at all, or a partial one stood and could not be read.</param>
/// <exception cref="ArgumentOutOfRangeException">A count or a version number is negative.</exception>
/// <exception cref="ArgumentException">The arguments contradict each other.</exception>
public sealed record ContentCatalogResetResult(
    int ActiveVersion,
    string? ServerManifestHash,
    string? ClientManifestHash,
    int VersionsDropped,
    int RowsDropped,
    string StoreEpoch,
    ContentCatalogPriorState PriorState = ContentCatalogPriorState.Read)
{
    /// <summary>
    /// Whether a whole catalog stood and was read, none stood at all, or a partial one stood and could not be
    /// read. Declared rather than generated so the CONSISTENCY RULES run on the way in, with every other
    /// value already in scope.
    /// </summary>
    public ContentCatalogPriorState PriorState { get; init; } = Validate(
        ActiveVersion, ServerManifestHash, ClientManifestHash, VersionsDropped, RowsDropped, PriorState);

    /// <summary>
    /// The one line an operator reads, and the same text the reset files in the new store's audit, so the
    /// console output and the stored record cannot say two different things. It is a fixed sentence around
    /// two 64-character hashes, a 32-character epoch and three numbers, well under the 4096 characters
    /// <c>catalog_audit.before_value</c> accepts.
    /// </summary>
    public string Summary => PriorState == ContentCatalogPriorState.Read
        ? FormattableString.Invariant(
            $"Catalog reset. It stood at {Stood}, and {VersionsDropped} versions and {RowsDropped} row revisions were dropped. The new store epoch is {StoreEpoch}.")
        : FormattableString.Invariant(
            $"Catalog reset. It stood at {Stood}, so nothing was dropped and nothing can be said about what it held. The new store epoch is {StoreEpoch}.");

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
        ContentCatalogPriorState priorState)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(activeVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(versionsDropped);
        ArgumentOutOfRangeException.ThrowIfNegative(rowsDropped);

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

        if (priorState != ContentCatalogPriorState.Read
            && (activeVersion != 0
                || serverManifestHash is not null
                || versionsDropped != 0
                || rowsDropped != 0))
        {
            throw new ArgumentException(
                "A reset that read no prior catalog cannot report a version, a manifest hash or anything dropped.",
                nameof(priorState));
        }

        return priorState;
    }

    /// <summary>What the store served, named by number and by both hashes when it served anything.</summary>
    string Stood => PriorState switch
    {
        ContentCatalogPriorState.Absent => "no catalog at all, because the database carried none of its tables",
        ContentCatalogPriorState.Unreadable => "a PARTIAL catalog, which no read could describe",
        _ when ActiveVersion == 0 => "nothing published",
        _ when ServerManifestHash is null || ClientManifestHash is null => FormattableString.Invariant(
            $"version {ActiveVersion}, whose version row was MISSING"),
        _ => FormattableString.Invariant(
            $"version {ActiveVersion}, server manifest {ServerManifestHash}, client manifest {ClientManifestHash}"),
    };
}
