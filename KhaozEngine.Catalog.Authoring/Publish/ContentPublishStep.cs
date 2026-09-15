namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The points a publish can be interrupted at (spec 6.11), copied from the map document's
/// <c>MapTiledSaveStep</c> so the crash-safety suites of both subsystems read the same way.
/// <para>
/// <b>It exists so a test can throw between two steps and assert what survived.</b> The invariant the whole
/// of section 6 is built around is one sentence, a crash at any point leaves either the old version or the
/// new one and never a torn one, and an enum a test can name is what turns that sentence into an assertion.
/// </para>
/// <para>
/// <b>Every value is reached.</b> The first six come from the pipeline of steps 1 to 8, and
/// <see cref="BeforeCommit"/>, <see cref="AfterCommit"/> and <see cref="DuringSweep"/> come from
/// <c>ContentPublishCommit</c>, which invokes them around the one transaction and inside the sweep. The
/// crash suite drives a kill at all nine against both stores, so there is no value here a test cannot stop a
/// publish at.
/// </para>
/// </summary>
public enum ContentPublishStep
{
    /// <summary>Step 3 is about to allocate. The candidate is built and no id has been issued.</summary>
    BeforeIdAllocation = 1,

    /// <summary>
    /// Every new row has its id. A reservation may have COMMITTED on its own by now, which is
    /// reserve-before-issue working: a crash here skips ids that were never issued.
    /// </summary>
    AfterIdAllocation = 2,

    /// <summary>Step 7 is about to encode. The candidate validated and the affected set is chosen.</summary>
    BeforeChunkWrite = 3,

    /// <summary>Every affected chunk is encoded, compressed and hashed. Nothing is written yet.</summary>
    AfterChunkWrite = 4,

    /// <summary>Step 8 is about to build both manifests.</summary>
    BeforeManifestWrite = 5,

    /// <summary>Both manifests are built and both hashes are computed.</summary>
    AfterManifestWrite = 6,

    /// <summary>
    /// Step 10's transaction is about to open. Every chunk file and both manifest files are written to the
    /// pack store at names nothing references yet, so a crash here leaves inert bytes and the old version.
    /// </summary>
    BeforeCommit = 7,

    /// <summary>The one transaction committed, so the new version is live and the draft is gone.</summary>
    AfterCommit = 8,

    /// <summary>Step 11 is pruning chunk files a previous failed attempt left unreferenced.</summary>
    DuringSweep = 9,
}
