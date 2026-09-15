using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;

namespace KhaozEngine.Benchmarks.CatalogCrash;

/// <summary>
/// The OUT OF PROCESS half of spec 15.6, <c>--catalog-crash-probe</c>: a child process really killed at each
/// of the nine <see cref="ContentPublishStep"/> points, against a real SQLite file and a real
/// <see cref="FileSystemPackStore"/> in a temp directory, with the same three properties asserted by
/// REOPENING afterwards.
/// <para>
/// <b>In-process hooks prove the ORDERING and a real kill proves the DURABILITY.</b> The in-process suite
/// (<c>KhaozEngine.Catalog.Tests/Publish/PublishCrashSafetyTests.cs</c>) throws from the hook, which unwinds
/// the stack and lets every <c>finally</c> and every connection close run. A kill does none of that: the
/// process stops mid-syscall, and what survives is whatever the database and the file system actually made
/// durable. Only the second one can see a commit that was never really committed.
/// </para>
/// <para>
/// It runs BOTH roles. Without <c>--child</c> it is the harness: seed, spawn, kill, reopen, assert, report.
/// With <c>--child</c> it is the victim: publish with a hook that prints a checkpoint at the named step and
/// then blocks forever, so the harness can stop it at exactly that instant.
/// </para>
/// </summary>
public static class CatalogCrashProbe
{
    /// <summary>The nine points a publish is interrupted at, in pipeline order.</summary>
    static readonly ContentPublishStep[] Steps =
    [
        ContentPublishStep.BeforeIdAllocation,
        ContentPublishStep.AfterIdAllocation,
        ContentPublishStep.BeforeChunkWrite,
        ContentPublishStep.AfterChunkWrite,
        ContentPublishStep.BeforeManifestWrite,
        ContentPublishStep.AfterManifestWrite,
        ContentPublishStep.BeforeCommit,
        ContentPublishStep.AfterCommit,
        ContentPublishStep.DuringSweep,
    ];

    /// <summary>Runs whichever role the arguments name.</summary>
    /// <param name="args">The command line, including <c>--catalog-crash-probe</c>.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="ArgumentException">An option is unknown or malformed.</exception>
    public static async Task RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        Parse(args, out bool child, out string? root, out ContentPublishStep? pauseAt);
        if (child)
        {
            await RunChildAsync(
                root ?? throw new ArgumentException("The child role requires --root.", nameof(args)),
                pauseAt ?? throw new ArgumentException("The child role requires --pause-at.", nameof(args)),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        Environment.ExitCode = await RunHarnessAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The harness: one kill per step, each in its own temp root, each reopened and checked, with one line of
    /// output per step and a summary that is also the exit code.
    /// </summary>
    static async Task<int> RunHarnessAsync(CancellationToken cancellationToken)
    {
        ContentVersionRecord control = await ControlAsync(cancellationToken).ConfigureAwait(false);
        Console.Out.WriteLine(FormattableString.Invariant(
            $"catalog-crash-probe: control publish is version {control.VersionNumber} at {Short(control.ServerManifestHash)}"));

        int recovered = 0;
        for (int i = 0; i < Steps.Length; i++)
        {
            ContentPublishStep step = Steps[i];
            string root = NewRoot();
            try
            {
                string outcome = await KillAndRecoverAsync(root, step, control, cancellationToken).ConfigureAwait(false);
                recovered++;
                Console.Out.WriteLine(FormattableString.Invariant($"catalog-crash-probe: {step,-19} {outcome}"));
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                Console.Out.WriteLine(FormattableString.Invariant($"catalog-crash-probe: {step,-19} FAILED {failure.Message}"));
            }
            finally
            {
                CatalogCrashScene.Delete(root);
            }
        }

        Console.Out.WriteLine(FormattableString.Invariant(
            $"catalog-crash-probe: {recovered}/{Steps.Length} steps left the store whole and republished to the same manifest"));
        return recovered == Steps.Length ? 0 : 1;
    }

    /// <summary>
    /// One step: seed, spawn, wait for the checkpoint, KILL, then reopen and assert the three properties.
    /// </summary>
    /// <param name="root">The probe root for this step.</param>
    /// <param name="step">The step to kill at.</param>
    /// <param name="control">The publish the retry is compared against.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The one-line outcome for the report.</returns>
    static async Task<string> KillAndRecoverAsync(
        string root,
        ContentPublishStep step,
        ContentVersionRecord control,
        CancellationToken cancellationToken)
    {
        await CatalogCrashScene.SeedAsync(root, cancellationToken).ConfigureAwait(false);

        using Process child = Spawn(root, step);
        try
        {
            await WaitForCheckpointAsync(child, step, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }

            await child.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        ContentTypeRegistry registry = CatalogCrashScene.Registry();

        // ValidateOnly, so a schema the kill damaged is a refusal here rather than a silent re-create.
        SqliteContentAuthoringStore store = await CatalogCrashScene
            .OpenAsync(root, registry, ContentAuthoringSchemaMode.ValidateOnly, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            int active = await store.GetActiveVersionAsync(cancellationToken).ConfigureAwait(false);
            if (active is not (1 or 2))
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"The store stands at {active}, which is neither the old version nor the new one."));
            }

            await AssertWholeAsync(store, active, cancellationToken).ConfigureAwait(false);
            await AssertEveryReferencedFileServesAsync(store, cancellationToken).ConfigureAwait(false);

            ContentVersionRecord landed = active == 2
                ? await RequireVersionAsync(store, 2, cancellationToken).ConfigureAwait(false)
                : await RetryAsync(store, registry, cancellationToken).ConfigureAwait(false);

            if (landed.VersionNumber != control.VersionNumber
                || !string.Equals(landed.ServerManifestHash, control.ServerManifestHash, StringComparison.Ordinal)
                || !string.Equals(landed.ClientManifestHash, control.ClientManifestHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"The retry landed version {landed.VersionNumber} at {Short(landed.ServerManifestHash)} and the uninterrupted publish produced version {control.VersionNumber} at {Short(control.ServerManifestHash)}."));
            }

            await AssertEveryReferencedFileServesAsync(store, cancellationToken).ConfigureAwait(false);
            return FormattableString.Invariant(
                $"killed at version {active}, {(active == 2 ? "committed" : "republished")} to {Short(landed.ServerManifestHash)}, every referenced file serves");
        }
        finally
        {
            store.Dispose();
        }
    }

    /// <summary>The same publish with nothing interrupting it, which every retry is compared against.</summary>
    static async Task<ContentVersionRecord> ControlAsync(CancellationToken cancellationToken)
    {
        string root = NewRoot();
        try
        {
            await CatalogCrashScene.SeedAsync(root, cancellationToken).ConfigureAwait(false);
            ContentTypeRegistry registry = CatalogCrashScene.Registry();
            SqliteContentAuthoringStore store = await CatalogCrashScene
                .OpenAsync(root, registry, ContentAuthoringSchemaMode.ValidateOnly, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await CatalogCrashScene.Commit(store, registry)
                    .PublishAsync(CatalogCrashScene.Request(1), cancellationToken).ConfigureAwait(false);
                return await RequireVersionAsync(store, 2, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                store.Dispose();
            }
        }
        finally
        {
            CatalogCrashScene.Delete(root);
        }
    }

    /// <summary>
    /// The CHILD role: publish the staged draft with a hook that announces the named step and then blocks, so
    /// the harness kills this process at exactly that point. It never returns normally at the paused step.
    /// </summary>
    static async Task RunChildAsync(string root, ContentPublishStep pauseAt, CancellationToken cancellationToken)
    {
        ContentTypeRegistry registry = CatalogCrashScene.Registry();
        SqliteContentAuthoringStore store = await CatalogCrashScene
            .OpenAsync(root, registry, ContentAuthoringSchemaMode.ValidateOnly, cancellationToken)
            .ConfigureAwait(false);

        int paused = 0;
        ContentPublishCommit commit = CatalogCrashScene.Commit(store, registry, step =>
        {
            if (step != pauseAt || Interlocked.Exchange(ref paused, 1) != 0)
            {
                return;
            }

            Console.Out.WriteLine(CatalogCrashScene.Checkpoint + " " + step);
            Console.Out.Flush();

            // The harness kills this process here. The read never returns, because nothing is ever written to
            // this child's input.
            _ = Console.In.ReadLine();
        });

        await commit.PublishAsync(CatalogCrashScene.Request(1), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The store read WHOLE: the old version with its draft intact, or the new one with it gone.</summary>
    static async Task AssertWholeAsync(
        SqliteContentAuthoringStore store,
        int active,
        CancellationToken cancellationToken)
    {
        ContentRowPage rows = await store
            .ListRowsAsync(CatalogCrashScene.Thing, 0, null, true, 0, 100, cancellationToken).ConfigureAwait(false);
        ContentDraft? draft = await store.GetOpenDraftAsync(cancellationToken).ConfigureAwait(false);
        long first = rows.Rows.Count > 0 ? rows.Rows[0].Fields[0].Number : -1;

        if (active == 1)
        {
            if (await store.GetVersionAsync(2, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new InvalidOperationException("The pointer is at version 1 and a version 2 row is there, which is a torn commit.");
            }

            if (first != 11 || draft is null || draft.EditCount != 1)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"The store is at version 1 and carries row value {first} with {draft?.EditCount ?? 0} draft edit(s), which is not the state before the publish."));
            }

            return;
        }

        if (first != 99 || draft is not null)
        {
            throw new InvalidOperationException(FormattableString.Invariant(
                $"The store is at version 2 and carries row value {first} with {draft?.EditCount ?? 0} draft edit(s), which is not the state after the publish."));
        }
    }

    /// <summary>Every hash any version references is in the pack and readable, and so is every pointer.</summary>
    static async Task AssertEveryReferencedFileServesAsync(
        SqliteContentAuthoringStore store,
        CancellationToken cancellationToken)
    {
        IPackStore pack = store.PackStore
            ?? throw new InvalidOperationException("The probe store was opened with no pack target.");
        IReadOnlyList<ContentVersionRecord> versions = await store
            .ListVersionsAsync(cancellationToken).ConfigureAwait(false);

        for (int i = 0; i < versions.Count; i++)
        {
            int named = 0;
            await foreach (string hash in pack.ListAsync(versions[i].VersionNumber, cancellationToken).ConfigureAwait(false))
            {
                named++;
                if (await pack.GetAsync(hash, cancellationToken).ConfigureAwait(false) is null)
                {
                    throw new InvalidOperationException(FormattableString.Invariant(
                        $"Version {versions[i].VersionNumber} names {Short(hash)} and the pack cannot serve it."));
                }
            }

            if (named == 0)
            {
                throw new InvalidOperationException(FormattableString.Invariant(
                    $"Version {versions[i].VersionNumber} enumerates no file at all, so its pointer or a manifest is unreadable."));
            }
        }
    }

    static async Task<ContentVersionRecord> RetryAsync(
        SqliteContentAuthoringStore store,
        ContentTypeRegistry registry,
        CancellationToken cancellationToken)
    {
        await CatalogCrashScene.Commit(store, registry)
            .PublishAsync(CatalogCrashScene.Request(1), cancellationToken).ConfigureAwait(false);
        return await RequireVersionAsync(store, 2, cancellationToken).ConfigureAwait(false);
    }

    static async Task<ContentVersionRecord> RequireVersionAsync(
        SqliteContentAuthoringStore store,
        int versionNumber,
        CancellationToken cancellationToken)
        => await store.GetVersionAsync(versionNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(FormattableString.Invariant(
                $"The store holds no version {versionNumber}."));

    /// <summary>Reads the child's output until it announces the step, or fails if it finishes first.</summary>
    static async Task WaitForCheckpointAsync(Process child, ContentPublishStep step, CancellationToken cancellationToken)
    {
        string expected = CatalogCrashScene.Checkpoint + " " + step;
        while (await child.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
        {
            if (string.Equals(line, expected, StringComparison.Ordinal))
            {
                return;
            }
        }

        string error = await child.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException(FormattableString.Invariant(
            $"The child never reached {step}. Its error output was: {error}"));
    }

    static Process Spawn(string root, ContentPublishStep step)
    {
        string probe = typeof(CatalogCrashProbe).Assembly.Location;
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(probe);
        start.ArgumentList.Add("--catalog-crash-probe");
        start.ArgumentList.Add("--child");
        start.ArgumentList.Add("--root");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add("--pause-at");
        start.ArgumentList.Add(step.ToString());
        return Process.Start(start)
            ?? throw new InvalidOperationException("The catalog crash probe child did not start.");
    }

    static string NewRoot()
        => Path.Combine(Path.GetTempPath(), "kec-crash-probe-" + Guid.NewGuid().ToString("n"));

    static string Short(string hash) => hash.Length <= 12 ? hash : hash[..12];

    static void Parse(
        IReadOnlyList<string> args,
        out bool child,
        out string? root,
        out ContentPublishStep? pauseAt)
    {
        child = false;
        root = null;
        pauseAt = null;
        bool mode = false;

        for (int i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--catalog-crash-probe":
                    if (mode)
                    {
                        throw new ArgumentException("Crash probe mode may be specified once.", nameof(args));
                    }

                    mode = true;
                    break;
                case "--child":
                    child = true;
                    break;
                case "--root":
                    root = Value(args, ref i, "--root");
                    if (!Path.IsPathFullyQualified(root))
                    {
                        throw new ArgumentException("The crash probe root must be absolute.", nameof(args));
                    }

                    break;
                case "--pause-at":
                    string name = Value(args, ref i, "--pause-at");
                    pauseAt = Enum.TryParse(name, ignoreCase: false, out ContentPublishStep parsed)
                        ? parsed
                        : throw new ArgumentException(
                            "The pause step must be one of " + string.Join(", ", Steps) + ".", nameof(args));
                    break;
                default:
                    throw new ArgumentException(
                        string.Create(CultureInfo.InvariantCulture, $"Unknown crash probe option '{args[i]}'."),
                        nameof(args));
            }
        }

        if (!mode)
        {
            throw new ArgumentException("Specify --catalog-crash-probe.", nameof(args));
        }
    }

    static string Value(IReadOnlyList<string> args, ref int index, string option)
    {
        if (++index >= args.Count)
        {
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"Option '{option}' requires a value."), nameof(args));
        }

        return args[index];
    }
}
