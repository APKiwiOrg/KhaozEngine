using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Primitives;

namespace KhaozEngine.Catalog;

/// <summary>
/// How one run of the client fetch loop ended (spec 8.7, 8.8). Everything that is not
/// <see cref="Complete"/> leaves the client AT THE DOOR: there is no state in which a client holds half a
/// version, which is what makes the door comparison a hash equality rather than a negotiation.
/// </summary>
public enum ContentFetchOutcome
{
    /// <summary>Every hash the manifest names is held and verified, so the client may reconnect.</summary>
    Complete = 0,

    /// <summary>
    /// The manifest is absent, does not decode, or does not digest to the name it was fetched under. It was
    /// refetched once and the client STOPS, because it cannot tell a bad CDN from a bad configuration.
    /// </summary>
    ManifestUnreadable,

    /// <summary>The pack was written by a format generation this build cannot read. Ask the player to update.</summary>
    GenerationTooNew,

    /// <summary>This build is below the version's <c>MinimumClientBuild</c>. Ask the player to update.</summary>
    ClientBuildTooOld,

    /// <summary>At least one chunk did not arrive or did not verify after every attempt.</summary>
    ChunksMissing,
}

/// <summary>
/// One object the fetch gave up on, with the reason the verify itself gave. The reason is never a token this
/// loop invented over the top of a more precise one: a short body, a wrong body and an absent body are three
/// different lines for an operator, and collapsing them would be throwing away the only diagnosis the client
/// has.
/// </summary>
/// <param name="Hash">The content address that failed.</param>
/// <param name="Reason">The stable reason token, the store's or the decoder's own.</param>
public sealed record ContentFetchFailure(string Hash, string Reason);

/// <summary>
/// What the fetch has done so far, reported while it runs (spec 8.7 step 6) and carried on the result. The
/// counts are against ONE required set, so a client draws one progress bar for the whole cold start rather
/// than one per attempt.
/// </summary>
/// <param name="ChunksRequired">Every hash the manifest names, the manifest itself excluded.</param>
/// <param name="ChunksCached">How many of those the cache already held when the first attempt computed the missing set.</param>
/// <param name="ChunksFetched">How many arrived over the wire during this call, across every attempt.</param>
/// <param name="ChunksFailed">How many are still missing, which is 0 on a complete fetch.</param>
/// <param name="BytesFetched">The stored bytes that came over the wire, which is what the transfer costs.</param>
/// <param name="Attempt">Which attempt this report belongs to, from 1.</param>
public sealed record ContentFetchProgress(
    int ChunksRequired,
    int ChunksCached,
    int ChunksFetched,
    int ChunksFailed,
    long BytesFetched,
    int Attempt)
{
    /// <summary>How many of the required set are held and verified, cached and fetched alike.</summary>
    public int ChunksHeld => ChunksCached + ChunksFetched;

    /// <summary>How many of the required set are still outstanding.</summary>
    public int ChunksRemaining => ChunksRequired - ChunksHeld;

    /// <summary>The held fraction, 0 to 1, and 1 for a version that names nothing.</summary>
    public double Fraction => ChunksRequired <= 0 ? 1.0 : ChunksHeld / (double)ChunksRequired;
}

/// <summary>
/// What a client fetch is configured with. Every one of these is the CONSUMER's: the build ordinal is the
/// game's own, the languages are the player's, and the concurrency is a property of a home connection
/// rather than of a catalog.
/// </summary>
public sealed record ContentFetchOptions
{
    /// <summary>
    /// The fetches in flight at once, spec 8.7 step 5. FOUR, because a home connection saturates at two or
    /// three streams and unbounded parallelism against a CDN buys nothing over a link that is the floor.
    /// </summary>
    public int Concurrency { get; init; } = 4;

    /// <summary>
    /// This build's client ordinal, compared against the version's <c>MinimumClientBuild</c>. It is the
    /// consumer's own number and the engine never supplies one, so the default of 0 fails closed against any
    /// version that names a floor at all.
    /// </summary>
    public int ClientBuild { get; init; }

    /// <summary>
    /// How many times the whole missing set is attempted before the fetch reports what is still missing.
    /// Each chunk is also retried once WITHIN an attempt, which is a different thing: that retry is for a
    /// transfer that mangled bytes, and this one is for a source that was not serving them yet.
    /// </summary>
    public int Attempts { get; init; } = 3;

    /// <summary>
    /// The FIRST wait between attempts, doubling per attempt up to <see cref="BackoffCeiling"/> and then
    /// multiplied by a uniform draw in [0, 1] (spec 13.4). Zero runs the attempts back to back, which is
    /// what a test wants and what a client never sets.
    /// </summary>
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest that doubling may reach, before the jitter draw is taken against it.</summary>
    public TimeSpan BackoffCeiling { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Where the backoff's jitter is drawn from. FULL jitter is the whole point of it: without a draw, a
    /// world restart puts every client of a population on one retry schedule, and the backoff turns a
    /// thundering herd into a synchronized one instead of spreading it.
    /// <para>
    /// The default is the OS source, which is the safe default rather than a convenience: the failure a
    /// default source can cause here is a predictable retry schedule, and a seeded source is the thing a
    /// test passes in deliberately.
    /// </para>
    /// </summary>
    public IRandomSource Random { get; init; } = new CryptographicRandomSource();

    /// <summary>
    /// How the loop waits, which is <see cref="Task.Delay(TimeSpan, CancellationToken)"/> in a client and a
    /// recorder in a test: a backoff test that actually slept would be a minute of suite time per assertion.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    /// <summary>
    /// The language tags to fetch text chunks for, or empty for every language the version ships. A client
    /// downloads only the languages it wants (spec 7.6), which is why they are listed in the manifest with
    /// their own hashes.
    /// </summary>
    public IReadOnlyList<string> Languages { get; init; } = [];

    /// <summary>Where progress goes while the fetch runs, or null for a fetch nobody is watching.</summary>
    public IProgress<ContentFetchProgress>? Progress { get; init; }
}

/// <summary>
/// What one call of the fetch loop did. <see cref="Success"/> is true for <see cref="ContentFetchOutcome.Complete"/>
/// and for nothing else, and there is deliberately no member here that hands back a snapshot, a runtime or a
/// reader: a partial download never becomes a partial catalog, so the only thing an incomplete fetch produces
/// is a reason and a progress report.
/// </summary>
public sealed class ContentFetchResult
{
    internal ContentFetchResult(
        ContentFetchOutcome outcome,
        ContentVersionIdentity version,
        ContentManifest? manifest,
        string? hash,
        string? reason,
        int attempts,
        ContentFetchProgress progress,
        IReadOnlyList<ContentFetchFailure> failures)
    {
        Outcome = outcome;
        Version = version;
        Manifest = manifest;
        Hash = hash;
        Reason = reason;
        Attempts = attempts;
        Progress = progress;
        Failures = failures;
    }

    /// <summary>True only when every hash the manifest names is held and verified.</summary>
    public bool Success => Outcome == ContentFetchOutcome.Complete;

    /// <summary>How the fetch ended.</summary>
    public ContentFetchOutcome Outcome { get; }

    /// <summary>The version the door named, which is what the fetch was for.</summary>
    public ContentVersionIdentity Version { get; }

    /// <summary>The version's manifest, or null when the manifest itself is what failed.</summary>
    public ContentManifest? Manifest { get; }

    /// <summary>The address the fetch stopped at, or null on a complete fetch.</summary>
    public string? Hash { get; }

    /// <summary>The stable reason token, or null on a complete fetch.</summary>
    public string? Reason { get; }

    /// <summary>The version's minimum client build, or 0 when no manifest was read.</summary>
    public int MinimumClientBuild => Manifest is null ? 0 : (int)Manifest.MinimumClientBuild;

    /// <summary>How many attempts over the missing set this call made.</summary>
    public int Attempts { get; }

    /// <summary>The final progress, which is also the last report the sink was handed.</summary>
    public ContentFetchProgress Progress { get; }

    /// <summary>Every object the fetch gave up on, empty on a complete fetch.</summary>
    public IReadOnlyList<ContentFetchFailure> Failures { get; }
}
