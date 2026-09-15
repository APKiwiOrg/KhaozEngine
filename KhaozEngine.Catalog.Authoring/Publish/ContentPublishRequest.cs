namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// What a caller asks a publish for (spec 10.6). The pipeline that CONSUMES it is
/// <c>ContentPublisher</c>, and this type exists ahead of it because
/// <see cref="IContentAuthoringStore.PublishAsync"/> names it, so a provider written against the seam and a
/// publisher written against the pipeline agree from the first commit.
/// <para>
/// <b><see cref="ExpectedBaseVersion"/> is optimistic concurrency and it is REQUIRED.</b> Two consoles
/// cannot both publish the same draft: the second one's expectation is stale and it is refused with both
/// numbers named. That is the same shape as a journal stream mutation's expected version, and it turns a
/// race into an error message.
/// </para>
/// <para>
/// <b>The minimum builds are CONSUMER supplied and the engine never interprets them beyond comparing</b>
/// (contracts 7.4). Omitted, they carry FORWARD the previous version's values rather than resetting to 0,
/// so a publisher who has nothing to say about builds says nothing. The format generation is NOT on this
/// request at all: it is read from the engine and never supplied by a caller.
/// </para>
/// </summary>
/// <param name="Actor">What the engine authenticated, which is the bearer token's holder.</param>
/// <param name="Operator">The identity the console forwarded, empty when it forwarded none (spec 10.10).</param>
/// <param name="Note">The publisher's note, at most 1,024 characters.</param>
/// <param name="ExpectedBaseVersion">The version the draft was opened against. Required.</param>
/// <param name="MinimumServerBuild">The new minimum server build, or null to carry the previous forward.</param>
/// <param name="MinimumClientBuild">The new minimum client build, or null to carry the previous forward.</param>
public sealed record ContentPublishRequest(
    string Actor,
    string Operator,
    string Note,
    int ExpectedBaseVersion,
    int? MinimumServerBuild = null,
    int? MinimumClientBuild = null);

/// <summary>
/// What a successful publish produced (spec 10.6's 200 response): the new version's identity on both sides,
/// and the work it cost. The counts are what let an operator see that an edit to one row rewrote one chunk
/// and reused the other sixty-three, which is the operator-facing half of the one-item-edit budget.
/// </summary>
/// <param name="VersionNumber">The number the commit assigned.</param>
/// <param name="ServerManifestHash">The server manifest digest, lower hex.</param>
/// <param name="ClientManifestHash">The client manifest digest, lower hex.</param>
/// <param name="FormatGeneration">The engine's pack format generation this version was written at.</param>
/// <param name="ChunksWritten">Chunks whose bytes were written, which is the download an adopting client pays.</param>
/// <param name="ChunksReused">Chunks carried forward unchanged, which a client already holding them refetches never.</param>
/// <param name="BytesWritten">Stored bytes written across every chunk and both manifests.</param>
/// <param name="RulesAppended">Remap rules appended, which is 0 for a publish that retires and forks nothing.</param>
/// <param name="ElapsedMilliseconds">Wall clock the publish took, for the budget in the operator's response.</param>
public sealed record ContentPublishResult(
    int VersionNumber,
    string ServerManifestHash,
    string ClientManifestHash,
    int FormatGeneration,
    int ChunksWritten,
    int ChunksReused,
    long BytesWritten,
    int RulesAppended,
    long ElapsedMilliseconds)
{
    /// <summary>The number and the SERVER manifest hash, the pair a server boot names the version by.</summary>
    public ContentVersionIdentity ServerIdentity => new(VersionNumber, ServerManifestHash);

    /// <summary>The number and the CLIENT manifest hash, the pair the connect door carries.</summary>
    public ContentVersionIdentity ClientIdentity => new(VersionNumber, ClientManifestHash);
}
