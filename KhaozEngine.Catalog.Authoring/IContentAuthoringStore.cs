using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// One page of rows, which is what keeps a console PAGING rather than fetching a million rows. The total is
/// carried beside the page because a console that cannot see how many rows it did not get shows the first
/// screenful and says nothing, which is a defect that has already shipped once in a consumer's bag UI.
/// </summary>
/// <param name="VersionNumber">The version the rows were read at, which resolves the caller's 0.</param>
/// <param name="Total">How many rows matched, before skip and take.</param>
/// <param name="Rows">The page, ordered by definition id.</param>
public sealed record ContentRowPage(int VersionNumber, int Total, IReadOnlyList<ContentRow> Rows);

/// <summary>
/// ONE row version out of a definition's history (spec 3.7). Rows are temporal, so the history of a
/// definition is the ordered set of its rows and "what did item 12 look like at version 40" is a QUERY
/// rather than an audit replay.
/// </summary>
/// <param name="Row">The row as it stood.</param>
/// <param name="ValidFromVersion">The version it became valid in.</param>
/// <param name="ReplacedInVersion">The version it was replaced in, or null while it is still live.</param>
/// <param name="FamilyId">The family it was allocated from, or null.</param>
public sealed record ContentRowRevision(
    ContentRow Row,
    int ValidFromVersion,
    int? ReplacedInVersion,
    long? FamilyId);

/// <summary>
/// The authoring provider seam (spec 2.3): draft edits, publish, version listing, audit, id allocation, and
/// bulk import and export. EVERY backend implements this one shape, which is why the member list is fixed
/// here rather than growing per provider.
/// <para>
/// <b>Nothing in this package implements it against a database.</b> The package is pure .NET with no SQL,
/// so a game client never pulls a database dependency to read a pack, and the SQLite and SQL Server backends
/// are opt-in sibling packages that reference this one.
/// </para>
/// <para>
/// <b>Reserve before issue</b> (contracts 6.2) is the one ordering rule an implementation may not invert. A
/// range is RESERVED durably BEFORE any id in it is issued, so the worst a crash can do is skip a block of
/// ids that were never issued, and it can never reissue one. Persisting the reservation after issuing leaves
/// a window in which a crash hands the next boot an id it has already put on a row, and a duplicate
/// definition id is the one failure that rule exists to prevent.
/// </para>
/// <para>
/// <b>A published version is immutable and remap rules are append only</b> (contracts 8.1). There is no
/// update path and no delete path for a rule anywhere in any provider, and this seam offers neither.
/// </para>
/// <para>
/// Every member is asynchronous because both backends are, and every member takes a cancellation token so a
/// console request that goes away does not hold a transaction open behind it.
/// </para>
/// </summary>
public interface IContentAuthoringStore
{
    /// <summary>
    /// Opens the store's schema under the given mode, creating it only under
    /// <see cref="ContentAuthoringSchemaMode.AutoCreate"/> and validating it under both.
    /// </summary>
    /// <param name="mode">Whether an empty or mismatched database may be created into.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The schema is absent or mismatched, naming the object and the required migration.</exception>
    Task InitializeAsync(ContentAuthoringSchemaMode mode, CancellationToken cancellationToken = default);

    /// <summary>The schema version the open database carries, which a migration compares against.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The IDENTITY of this database, minted once at schema creation. It is what makes "version 12"
    /// answerable: an import into an empty database restarts the version line at 1, so two databases can
    /// hold a version 12 that share no history, and the epoch is what tells them apart.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<string> GetStoreEpochAsync(CancellationToken cancellationToken = default);

    /// <summary>The version the last publish committed, or 0 when the database has published none.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<int> GetActiveVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The operator's HOLD, or null for the ordinary no-pin state. A server's own config pin wins over this
    /// one, always, and the active version is the fallback below both.
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<int?> GetPinnedVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes or clears the operator's hold. This is the only lever between publishing and restarting:
    /// pinning is how a publish is staged for a later restart and unpinning is how a server catches up.
    /// </summary>
    /// <param name="version">The version to hold at, or null to clear the hold.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded, empty when it forwarded none.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException"><paramref name="version"/> names a version that does not exist.</exception>
    Task SetPinnedVersionAsync(
        int? version,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default);

    /// <summary>Every published version, newest first.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<ContentVersionRecord>> ListVersionsAsync(CancellationToken cancellationToken = default);

    /// <summary>One published version's row, or null when the store holds no such version.</summary>
    /// <param name="versionNumber">The version number.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ContentVersionRecord?> GetVersionAsync(int versionNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads one published version as a snapshot, decoding through the registry's codecs. The registry is
    /// handed in rather than held, because it is per instance and never an ambient static.
    /// </summary>
    /// <param name="versionNumber">The version to load.</param>
    /// <param name="registry">The registry whose codecs decode the rows.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The version does not exist.</exception>
    Task<ContentSnapshot> LoadSnapshotAsync(
        int versionNumber,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken = default);

    /// <summary>The ONE open draft with its edits expanded, or null when none is open.</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ContentDraft?> GetOpenDraftAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies edits to the open draft, opening one against the active version when none is open. Every edit
    /// in one call lands in ONE transaction or none of them does, so a batch save from a grid is atomic, and
    /// the edits are checked against the schema AT THE BOUNDARY rather than at publish.
    /// </summary>
    /// <param name="edits">The edits to apply, in order.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded, empty when it forwarded none.</param>
    /// <param name="note">The operator's note, at most 1,024 characters.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">An edit names a field the schema does not declare, or a target the draft already holds under a different operation.</exception>
    Task<ContentDraft> ApplyEditsAsync(
        IReadOnlyList<ContentEdit> edits,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the open draft and its edits, writing one audit row carrying the edit count so a discarded
    /// draft leaves a trace.
    /// </summary>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task DiscardDraftAsync(string actor, string operatorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The BASE version as the publish pipeline needs it, read under whatever lock the implementation holds
    /// a publish behind: the active version's number, its live rows, the full rule list, its chunk rows at
    /// every side, its languages and its two minimum builds.
    /// <para>
    /// It exists as a member rather than as something the publisher reads for itself because the version the
    /// candidate is built against and the version the transaction commits against must be one read. A
    /// publisher that assembled its own baseline out of the seam's other members would be reading each half
    /// at a different moment, which is exactly the race the commit's own confirmation closes.
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ContentPublishBaseline> ReadPublishBaselineAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Step 10 of spec 6.1, the ONE transaction, and the only member here that moves the active pointer. In
    /// order inside it: confirm the version number, insert the version row, apply every temporal row change,
    /// append every remap rule at the sequence above the highest, insert every chunk row one per side, insert
    /// every audit row, delete the draft, then move the active pointer LAST.
    /// <para>
    /// <b>It CONFIRMS the number rather than trusting it.</b> The plan digested its version number into both
    /// manifest hashes at step 8, so the transaction re-reads the highest published number and refuses when
    /// the plan's is not the next one. An implementation that leases a connection per call holds no lock
    /// across steps 1 to 10, and this is the check that catches the base moving underneath such a plan.
    /// </para>
    /// <para>
    /// A reader that sees the new active version is guaranteed to see every row, rule, chunk and audit entry
    /// of it, because they committed together. The pointer moves for the NEXT boot: a running server keeps
    /// serving the version it loaded.
    /// </para>
    /// </summary>
    /// <param name="plan">The plan steps 1 to 8 produced, whose pack files step 9 has already written.</param>
    /// <param name="request">The publish request, whose actor, operator and note the version row and the audit carry.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The version row the transaction inserted.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ContentAuthoringException">The plan did not validate, or the highest published version moved under it.</exception>
    Task<ContentVersionRecord> CommitPublishAsync(
        ContentPublishPlan plan,
        ContentPublishRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes the open draft as a new immutable version: validate, allocate, encode, write the pack, then
    /// ONE transaction that moves the active pointer together with every row, rule, chunk and audit entry.
    /// <para>
    /// <b>An exception thrown AFTER that transaction means the version may already be live.</b> The sweep of
    /// step 11 runs past the only commit point there is, so a throw from it leaves a published version behind
    /// a failed call. A caller reads the active version, or republishes: the same draft against a base that
    /// has moved is refused, which makes the retry idempotent rather than a second version.
    /// </para>
    /// </summary>
    /// <param name="request">The publish request, carrying the required expected base version.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The base version moved, or the candidate did not validate.</exception>
    Task<ContentPublishResult> PublishAsync(
        ContentPublishRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// BUILDS A DRAFT that would restore an earlier version, rather than publishing one directly, so the
    /// operator reviews the diff and publishes it. A row live at the target and RETIRED since is refused: a
    /// retire is irreversible for pages already migrated past it, and the way out is an ordinary add under a
    /// new key plus a replacement rule.
    /// </summary>
    /// <param name="targetVersion">The version to restore the field values of.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">A row live at the target has been retired since.</exception>
    Task<ContentDraft> RollbackToAsync(
        int targetVersion,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One page of a type's rows at a version. <paramref name="versionNumber"/> 0 means the current live
    /// set, which is the active version when one exists and the DRAFT-APPLIED set when a draft is open.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="versionNumber">The version, or 0 for the current live set.</param>
    /// <param name="keyPrefix">An ordinal key prefix filter, or null for every row.</param>
    /// <param name="includeRetired">Whether retired rows are in the page.</param>
    /// <param name="skip">How many matching rows to skip.</param>
    /// <param name="take">How many to return, capped by the implementation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<ContentRowPage> ListRowsAsync(
        ContentTypeId type,
        int versionNumber,
        string? keyPrefix,
        bool includeRetired,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One definition's full history, oldest first. This is the temporal model's payoff: an operator asking
    /// "when did this price change and what was it before" gets an answer from the row table rather than
    /// from an audit reconstruction.
    /// </summary>
    /// <param name="type">The content type.</param>
    /// <param name="definitionId">The definition id.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<ContentRowRevision>> GetRowHistoryAsync(
        ContentTypeId type,
        int definitionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Audit entries, newest first. A <paramref name="type"/> whose value is 0 means every type, and a
    /// <paramref name="definitionId"/> of 0 means every row, so the two together read the whole audit.
    /// </summary>
    /// <param name="type">The content type to filter to, or type id 0 for every type.</param>
    /// <param name="definitionId">The definition id to filter to, or 0 for every row.</param>
    /// <param name="skip">How many entries to skip.</param>
    /// <param name="take">How many to return, capped by the implementation.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<ContentAuditEntry>> ListAuditAsync(
        ContentTypeId type,
        int definitionId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues a contiguous range of definition ids for one type, RESERVING durably before issuing, and
    /// returns the FIRST id of the range. The range is <c>[first, first + count - 1]</c>.
    /// </summary>
    /// <param name="type">The content type to allocate from.</param>
    /// <param name="count">How many ids, at least 1.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The range would cross the type's declared id ceiling, naming the type, the ceiling and the high-water mark.</exception>
    Task<int> AllocateAsync(ContentTypeId type, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Issues ONE id from a family's blocks in ordinal order, reserving a new aligned block when every block
    /// is full. Reserving a block also advances the type's issued mark to the new block's top, which is what
    /// keeps the plain counter from walking under the block and reissuing an id inside it.
    /// </summary>
    /// <param name="familyId">The family to allocate from.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">A new block's top would cross the type's declared id ceiling.</exception>
    Task<int> AllocateInFamilyAsync(long familyId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every family of one type with its blocks in ordinal order, or every family of every type when
    /// <paramref name="type"/> carries type id 0, which is the same "0 means all" convention the audit read
    /// uses.
    /// <para>
    /// A family is a READ as well as a write: a console shows one, and a bundle export carries every family
    /// with its blocks, so the seam owes a way to reach one that is not the allocator's own persistence.
    /// </para>
    /// </summary>
    /// <param name="type">The content type to filter to, or type id 0 for every type.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<IReadOnlyList<ContentFamily>> ListFamiliesAsync(
        ContentTypeId type,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a family and reserves its first aligned block. The block size is declared HERE and cannot be
    /// changed later, because changing it would move every id in the family.
    /// <para>
    /// <b>The family and its blocks are stamped with the version they will FIRST APPEAR IN</b>, which is the
    /// active version plus one, and never with the active version itself. Creating a family is an immediate
    /// action while the active version is 0 on a database that has published nothing, so stamping the active
    /// version would write a 0 against a column that begins at 1. Stamping the next number is also the truer
    /// statement: no published version carries the family until the one that is being authored.
    /// </para>
    /// </summary>
    /// <param name="type">The content type the family groups rows of.</param>
    /// <param name="familyKey">The family's key, unique within its type.</param>
    /// <param name="blockSize">A power of two between 16 and 65,536.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The key is taken, or the block size is not a legal power of two.</exception>
    Task<ContentFamily> CreateFamilyAsync(
        ContentTypeId type,
        string familyKey,
        int blockSize,
        string actor,
        string operatorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a whole bundle into an EMPTY database, publishing it as version 1. Empty means the version
    /// table holds no rows, and a non-empty database is refused with nothing written.
    /// </summary>
    /// <param name="bundle">The bundle to import.</param>
    /// <param name="actor">What the engine authenticated.</param>
    /// <param name="operatorId">The identity the console forwarded.</param>
    /// <param name="note">The operator's note.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The database already holds a published version.</exception>
    Task<ContentPublishResult> ImportBundleAsync(
        ContentBundle bundle,
        string actor,
        string operatorId,
        string note,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports one version as a bundle, ids included, so an import into an empty database reproduces the
    /// same rows, keys and IDS.
    /// </summary>
    /// <param name="versionNumber">The version to export.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="ContentAuthoringException">The version does not exist.</exception>
    Task<ContentBundle> ExportBundleAsync(int versionNumber, CancellationToken cancellationToken = default);
}
