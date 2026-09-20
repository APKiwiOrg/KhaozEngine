using System;
using System.Collections.Generic;
using System.IO;

namespace KhaozEngine.Catalog;

/// <summary>
/// Which row of spec 9.6's exit table a boot stopped at, or <see cref="None"/> when it did not. Every one of
/// the twelve is a REFUSAL: a missing or invalid content version fails the boot, and there is no runtime
/// fallback to code defaults anywhere (contracts 10.5), because a silent fallback catalog serves content no
/// version names and an outage is at least noticed.
/// </summary>
public enum ContentBootRefusal
{
    /// <summary>The boot ran to the end and published a runtime.</summary>
    None = 0,

    /// <summary>Step 2: no config pin, no pinned version and no active version.</summary>
    NoActiveVersion,

    /// <summary>Step 3: the manifest is absent, does not digest to its own name, or does not decode.</summary>
    ManifestUnreadable,

    /// <summary>Step 3: the manifest's embedded version number is not the version the boot resolved.</summary>
    ManifestVersionMismatch,

    /// <summary>Step 4: the pack was written by a newer format generation than this build reads.</summary>
    GenerationTooNew,

    /// <summary>Step 5: the version requires a newer server build than this one.</summary>
    ServerBuildTooOld,

    /// <summary>Step 6: a chunk is absent, or its bytes do not digest to the name they were fetched under.</summary>
    ChunkUnreadable,

    /// <summary>Step 6: the manifest names a type this build does not register, so nothing can decode its rows.</summary>
    TypeUnregistered,

    /// <summary>Step 6: a type this build registers is absent from the version, the same failure from the other side.</summary>
    TypeAbsentFromVersion,

    /// <summary>Step 7: a chunk verified and then failed to decode.</summary>
    ChunkDecodeFailed,

    /// <summary>Step 7b: a registered load index threw rather than returning a partial index.</summary>
    LoadIndexFailed,

    /// <summary>Step 8: the validator reported at least one finding on the decoded version.</summary>
    ValidatorFindings,

    /// <summary>Step 11: the world names a content key that is not live in the version.</summary>
    WorldKeyUnresolved,

    /// <summary>
    /// Step 3, BEFORE the manifest is fetched: the store's <c>versions/&lt;n&gt;</c> pointer names a manifest
    /// the version record does not. It is last rather than in step order because these values ship in a
    /// package, and inserting one in the middle would move every value under it.
    /// </summary>
    PackPointerMismatch,
}

/// <summary>
/// What a boot did: the published runtime, or the refusal and the exact lines spec 9.6 puts on stderr.
/// <para>
/// <b>It never exits the process.</b> The engine has no business deciding when a host dies, so the boot builds
/// the operator's lines and the exit code and hands them back, and the host writes them and exits. That is
/// also what makes the whole exit table testable in one assembly, in process, rather than one child process
/// per row.
/// </para>
/// <para>
/// <see cref="ExitCode"/> is 3 for every refusal, deliberately distinct from the 2 a consumer already returns
/// for a bad config, so an operator or a supervisor script tells a content failure from a config failure
/// without parsing text.
/// </para>
/// </summary>
public sealed class ContentBootResult
{
    /// <summary>The exit code every content refusal carries, spec 9.6.</summary>
    public const int ContentFailureExitCode = 3;

    static readonly string[] NoLines = [];

    ContentBootResult(
        bool success,
        ContentRuntime? runtime,
        ContentBootRefusal refusal,
        int step,
        IReadOnlyList<string> standardError)
    {
        Success = success;
        Runtime = runtime;
        Refusal = refusal;
        Step = step;
        StandardError = standardError;
    }

    /// <summary>The boot that published a runtime, which the host then builds its connect door over.</summary>
    /// <param name="runtime">The published version.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is null.</exception>
    public static ContentBootResult Ok(ContentRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return new ContentBootResult(true, runtime, ContentBootRefusal.None, 0, NoLines);
    }

    /// <summary>One refusal carrying its single stderr line.</summary>
    /// <param name="refusal">Which row of the exit table stopped the boot.</param>
    /// <param name="step">The boot step it stopped at, which is what tells two rows sharing a line apart.</param>
    /// <param name="line">The line, rendered exactly as spec 9.6's table writes it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="line"/> is null.</exception>
    public static ContentBootResult Refuse(ContentBootRefusal refusal, int step, string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return new ContentBootResult(false, null, refusal, step, [line]);
    }

    /// <summary>The validator's refusal, which is one line per finding and then the count line.</summary>
    /// <param name="refusal">Which row of the exit table stopped the boot.</param>
    /// <param name="step">The boot step it stopped at.</param>
    /// <param name="lines">Every line, in the order they are written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="lines"/> is null.</exception>
    public static ContentBootResult Refuse(ContentBootRefusal refusal, int step, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return new ContentBootResult(false, null, refusal, step, lines);
    }

    /// <summary>True when the version loaded, validated and was published into the holder.</summary>
    public bool Success { get; }

    /// <summary>The published runtime, or null on every refusal.</summary>
    public ContentRuntime? Runtime { get; }

    /// <summary>Which of the twelve refusals stopped the boot, or <see cref="ContentBootRefusal.None"/>.</summary>
    public ContentBootRefusal Refusal { get; }

    /// <summary>
    /// The boot step of spec 9.5 the refusal happened at, and 0 on success. Two rows of the exit table share
    /// one line shape, a chunk that would not fetch and a chunk that would not decode, and the step is what
    /// tells them apart without parsing the reason token. Step 7b reports as 7, because the number is the
    /// spec's and 7b is not one. <see cref="Refusal"/> names the row exactly either way.
    /// </summary>
    public int Step { get; }

    /// <summary>The lines to write to stderr, in order. Empty on success.</summary>
    public IReadOnlyList<string> StandardError { get; }

    /// <summary>3 for every refusal and 0 for a boot that published.</summary>
    public int ExitCode => Success ? 0 : ContentFailureExitCode;

    /// <summary>
    /// Writes every line to the host's error stream. The host still owns the exit, because a library that
    /// called <c>Environment.Exit</c> would be deciding the shutdown order of a process it knows nothing about.
    /// </summary>
    /// <param name="writer">Where the lines go, ordinarily <c>Console.Error</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="writer"/> is null.</exception>
    public void WriteStandardError(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        for (int i = 0; i < StandardError.Count; i++)
        {
            writer.WriteLine(StandardError[i]);
        }
    }
}
