using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

/// <summary>Optional seams of the single in-process test entry point. Unset values leave production behaviour.</summary>
internal readonly record struct CoverageRunSeams
{
    /// <summary>Bound of the diagnostic root and of each of its parts. Defaults to the evidence part bound.</summary>
    internal int? ReportByteLimit { get; init; }
    /// <summary>A synthetic publication fault for the first transaction the run attempts.</summary>
    internal CoveragePublicationFault? PublicationFault { get; init; }
    /// <summary>Deletes one chunk's checked private copy at the named stage, after admission.</summary>
    internal CoverageUnreadableChunk? UnreadableChunk { get; init; }
    /// <summary>Makes the session's scratch cleanup fail after it ran, as a locked file would.</summary>
    internal bool FailSessionCleanup { get; init; }
    /// <summary>Receives what the executable writes to standard error.</summary>
    internal TextWriter? Error { get; init; }
}

internal enum CoverageRunStage { NumericalWork, Diagnostics }
internal readonly record struct CoverageUnreadableChunk(int ChunkIndex, CoverageRunStage Stage);

public static class Program
{
    readonly record struct Limits(int Artifact, int Part, int Report, string Scratch);

    sealed class RunState(CoverageDiagnosticRun diagnostic)
    {
        internal CoverageDiagnosticRun Diagnostic { get; } = diagnostic;
        internal CoverageInputSession? Session { get; set; }
        internal List<string> ProtectedInputs { get; } = [];
        internal List<string> AcceptedOutputs { get; } = [];
    }

    /// <summary>0 complete accepted work, 1 validation/output failure, 2 malformed input.</summary>
    public static int Main(string[] args) => Run(args, CoverageBuildInputJson.MaxBytes);

    /// <summary>A reduced bound applies to every evidence root and part and to the diagnostic report. A larger logical
    /// document becomes a hashed set rather than a refusal.</summary>
    internal static int Run(string[] args, int artifactByteLimit) =>
        Execute(args, new(artifactByteLimit, artifactByteLimit, artifactByteLimit, Path.GetTempPath()), default);

    /// <summary>The one in-process test entry point. It runs the production state machine with bounds at or below the
    /// production bounds, a chosen scratch root and the optional seams. The public command line has no such flags.</summary>
    internal static int RunForTests(string[] args, int artifactByteLimit, int partByteLimit, string scratchDirectory,
        CoverageRunSeams seams = default) =>
        Execute(args, new(artifactByteLimit, partByteLimit, seams.ReportByteLimit ?? partByteLimit, scratchDirectory), seams);

    static int Execute(string[] args, Limits limits, CoverageRunSeams seams)
    {
        var stderr = seams.Error ?? Console.Error;
        var run = new RunState(new CoverageDiagnosticRun());
        try
        {
            int exit = RunAccepted(args, limits, seams, run, stderr);
            var diagnostic = run.Diagnostic;
            // A failed transaction that already carried the report leaves the previous report in place.
            if (exit != 1 || diagnostic.Path == null || diagnostic.Input == null || diagnostic.PublicationAttempted) return exit;
            if (!TryCollect(run, args[0], exit, seams, stderr)) return 2;
            // A refusal replaces only diagnostics, so the accepted table and evidence are protected like inputs.
            try { Publish([], null, (diagnostic, args[0], exit), [.. run.ProtectedInputs, .. run.AcceptedOutputs], limits, seams.PublicationFault); }
            catch (CoverageBuildValidationException error) { stderr.WriteLine(error.Reason); return 1; }
            return exit;
        }
        // The session outlives every pose read, staging and publication of this run, then removes its scratch.
        finally { DisposeSession(run, stderr); }
    }

    /// <summary>Outputs are already decided when scratch is removed, so a cleanup failure is reported on stderr and never
    /// replaces the run's exit code. The leftover is an owner-only directory under the temporary root.</summary>
    static void DisposeSession(RunState run, TextWriter stderr)
    {
        try { run.Session?.Dispose(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { stderr.WriteLine(error.GetType().Name); }
    }

    /// <summary>Collects the report before any output is staged. Collection decodes poses, so a pose that can no longer
    /// be read is reported as the input refusal it is, never as an output failure, and no report is published.</summary>
    static bool TryCollect(RunState run, string command, int exit, CoverageRunSeams seams, TextWriter stderr)
    {
        try
        {
            if (seams.UnreadableChunk is { Stage: CoverageRunStage.Diagnostics } fault) run.Session!.MakeChunkUnreadableForTests(fault.ChunkIndex);
            run.Diagnostic.CollectFor(command, exit);
            return true;
        }
        catch (CoverageBuildDataException error) { stderr.WriteLine($"{error.Reason} {error.Path}"); return false; }
    }

    static int RunAccepted(string[] args, Limits limits, CoverageRunSeams seams, RunState run, TextWriter stderr)
    {
        var diagnostic = run.Diagnostic;
        try
        {
            if (args.Length == 1 && args[0] is "version" or "--version") return CoverageToolVersion.Write();
            if (args.Length is not (7 or 9) || args[0] is not ("build" or "verify-samples" or "verify")) return 2;
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
                if (args[i] is not ("--manifest" or "--table" or "--evidence" or "--diagnostics") || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], Path.GetFullPath(args[i + 1]))) return 2;
            if (args.Length == 9 && (!options.ContainsKey("--diagnostics") || !options.ContainsKey("--manifest")
                || !options.ContainsKey("--table") || !options.ContainsKey("--evidence"))) return 2;
            if (args.Length == 7 && options.ContainsKey("--diagnostics")) return 2;
            string manifest = options["--manifest"], tablePath = options["--table"], evidencePath = options["--evidence"];
            diagnostic.Path = options.GetValueOrDefault("--diagnostics");
            bool windows = OperatingSystem.IsWindows();
            if (diagnostic.Path != null && (new[] { diagnostic.Path, manifest, tablePath, evidencePath }.Any(p => CoverageDiagnosticName.Unsupported(p, windows))
                || CoverageDiagnosticName.Collides(diagnostic.Path, [manifest, tablePath, evidencePath]))) return 2;
            // Outputs are compared by the identity the OS resolves, so links, aliases and hard links cannot hide a collision.
            // Output identities are strict. An input-side path the OS cannot identify reaches no output, and input
            // admission reports it as malformed input.
            bool verifiesTable = args[0] != "build";
            var evidenceIdentity = CoveragePathIdentity.Of(evidencePath);
            var tableIdentity = verifiesTable ? CoveragePathIdentity.TryOf(tablePath) : CoveragePathIdentity.Of(tablePath);
            var manifestIdentity = CoveragePathIdentity.TryOf(manifest);
            bool Reaches(CoveragePathIdentity.Destination? input) => input is { } value && (value == evidenceIdentity || value == tableIdentity);
            if (Reaches(manifestIdentity) || tableIdentity is { } table && table == evidenceIdentity)
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputAlias, "Output paths must be distinct from inputs and each other.");
            // Every chunk is copied, hashed and scanned before any asset binding or geometry. Later poses come only from
            // the checked private copies, one decoded pose at a time.
            run.Session = CoverageInputSession.Open(manifest, limits.Scratch);
            if (seams.FailSessionCleanup) run.Session.FailCleanupForTests();
            var input = run.Session.Campaign;
            if (diagnostic.Path != null && (input.SourcePaths.Any(p => CoverageDiagnosticName.Unsupported(p, windows))
                || CoverageDiagnosticName.Collides(diagnostic.Path, input.SourcePaths))) return 2;
            diagnostic.Input = input;
            // Source paths include the original chunks and their private copies.
            if (input.SourcePaths.Any(p => Reaches(CoveragePathIdentity.TryOf(p))))
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputAlias, "Outputs cannot overwrite checked inputs.");
            run.ProtectedInputs.AddRange([manifest, .. input.SourcePaths]);
            if (!verifiesTable) run.AcceptedOutputs.Add(tablePath);
            else run.ProtectedInputs.Add(tablePath);
            run.AcceptedOutputs.Add(evidencePath);
            // An interrupted earlier publication is finished or undone before any of its outputs is read or replaced.
            CoveragePublisher.Recover(evidencePath);
            if (diagnostic.Path != null) CoveragePublisher.Recover(diagnostic.Path);
            if (seams.UnreadableChunk is { Stage: CoverageRunStage.NumericalWork } fault) run.Session.MakeChunkUnreadableForTests(fault.ChunkIndex);
            return Accept(args[0], input, tablePath, evidencePath, limits, seams, run, stderr);
        }
        catch (CoverageVerificationException error)
        {
            diagnostic.Refuse(error.Reason.ToString());
            stderr.WriteLine(JsonSerializer.Serialize(new
            {
                Reason = error.Reason.ToString(),
                error.DomainId,
                error.DenseIndex,
                error.SampleId,
                error.StateKey,
                error.EntryIndex,
                error.CommandId,
                error.EquipmentInstanceId,
                error.Failure,
            }));
            return 1;
        }
        catch (CoverageToolStateException error) { diagnostic.Refuse(error.Reason.ToString()); stderr.WriteLine(error.Reason); return 1; }
        catch (CoverageBuildValidationException error) { diagnostic.Refuse(error.Reason.ToString()); stderr.WriteLine(error.Reason); return 1; }
        catch (CoverageBuildDataException error) { stderr.WriteLine($"{error.Reason} {error.Path}"); return 2; }
        catch (MalformedDataException error) { stderr.WriteLine(error.GetType().Name); return 2; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { stderr.WriteLine(error.GetType().Name); return 2; }
    }

    /// <summary>Numerical work, then staging, resume admission, report collection and one publication, all while the
    /// session is alive.</summary>
    static int Accept(string command, CoverageCampaign input, string tablePath, string evidencePath, Limits limits,
        CoverageRunSeams seams, RunState run, TextWriter stderr)
    {
        var diagnostic = run.Diagnostic;
        CoverageEvidenceManifest evidence;
        byte[]? tableBytes = null;
        CoverageRecessionTable? suppliedTable = null;
        if (command == "build")
        {
            var result = CoverageTableBuilder.Build(input);
            evidence = result.Evidence;
            tableBytes = CoverageRecessionTableBinary.Write(result.Table);
            CoverageBuildInputJson.CheckByteCapacity(tableBytes.LongLength, CoverageBuildInputJson.MaxBytes, tablePath);
        }
        else
        {
            CoveragePublicationJournal.EnsureReadable(tablePath);
            suppliedTable = CoverageRecessionTableBinary.Read(CoverageBuildInputJson.ReadBytes(tablePath), tablePath);
            diagnostic.Table = suppliedTable;
            evidence = CoverageSampleVerifier.Verify(input, suppliedTable);
        }
        diagnostic.Evidence = evidence;
        if (command == "verify")
        {
            // Run dense work to completion before choosing a blocking reason. Neither gate substitutes for the other.
            var replay = CoverageReplay.Verify(input, suppliedTable!);
            evidence.IncludeReplay(replay);
            diagnostic.Replay = replay;
            var failedSample = evidence.SelectedClaims.FirstOrDefault(c => !c.Valid);
            if (failedSample != null)
                throw new CoverageVerificationException(CoverageVerificationRefusal.SelectedSampleFailure, "Selected sample failed exact certification.")
                {
                    DomainId = input.Samples.Single(s => s.Id == failedSample.SampleId).DomainId,
                    SampleId = failedSample.SampleId,
                    StateKey = failedSample.StateKey,
                    EntryIndex = failedSample.EntryIndex,
                    Failure = failedSample.Failure
                };
            if (evidence.Holes.FirstOrDefault() is { } hole)
            {
                var sample = input.Samples.Single(s => s.Id == hole.SampleIds[0]);
                throw new CoverageVerificationException(CoverageVerificationRefusal.SampleHole, "A declared sample has no selected entry.")
                { DomainId = sample.DomainId, SampleId = sample.Id, StateKey = hole.StateKey, EntryIndex = -1 };
            }
            replay.EnsureAccepted();
        }
        if (!evidence.Complete) return 1;
        // The complete document is staged first, as one file or as a hashed set, and is what resume compares against.
        using var staged = Stage(() => CoverageArtifactWriter.Stage(evidencePath, evidence.CreateHeader(), evidence.WriteTo,
            limits.Scratch, limits.Artifact, limits.Part));
        // Old evidence is interpreted only once the current work is accepted.
        if (File.Exists(evidencePath)) Resume(evidencePath, staged, limits, diagnostic);
        if (diagnostic.Path != null && !TryCollect(run, command, 0, seams, stderr)) return 2;
        (string Path, byte[] Bytes)[] outputs = tableBytes == null ? [] : [(tablePath, tableBytes)];
        diagnostic.PublicationAttempted = diagnostic.Path != null;
        Publish(outputs, staged, diagnostic.Path == null ? null : (diagnostic, command, 0), run.ProtectedInputs, limits, seams.PublicationFault);
        return 0;
    }

    /// <summary>Opens the existing evidence root through the strict reader, so a set is checked part by part, records
    /// the exact root bytes read for diagnostics, then compares every logical record with the staged document.</summary>
    static void Resume(string evidencePath, CoverageStagedArtifact current, Limits limits, CoverageDiagnosticRun diagnostic)
    {
        using var existing = CoverageArtifactReader.Open(evidencePath, limits.Scratch, limits.Artifact);
        diagnostic.ResumedEvidenceSha256 = existing.RootSha256;
        using var actual = existing.OpenPayload();
        using var expected = current.OpenPayload();
        CoverageLogicalDocumentReader.Compare(actual, expected, CoverageArtifactKind.Evidence);
    }

    /// <summary>Staging failures are output failures. A pose read failure inside staging keeps its own input refusal.</summary>
    static CoverageStagedArtifact Stage(Func<CoverageStagedArtifact> stage)
    {
        try { return stage(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Output staging refused.", error);
        }
        catch (CoverageBuildDataException error) when (error.Reason == CoverageBuildInputRefusal.CapacityExceeded)
        {
            // An output whose index cannot name every part is an output failure, refused before anything is replaced.
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Output exceeds its artifact bound.", error);
        }
    }

    /// <summary>Hands the staged evidence, the table and an already collected report to the common publisher. The
    /// evidence root is published last as the commit point. The report is staged complete, as one file or as a set
    /// split at the report bound.</summary>
    static void Publish((string Path, byte[] Bytes)[] files, CoverageStagedArtifact? evidence,
        (CoverageDiagnosticRun Run, string Command, int Exit)? diagnostics, IReadOnlyList<string> protectedInputs, Limits limits,
        CoveragePublicationFault? fault)
    {
        string? scratch = null;
        CoverageStagedArtifact? report = null;
        try
        {
            scratch = CoverageArtifactSet.Scratch(limits.Scratch);
            if (diagnostics is { } value)
                report = Stage(() => value.Run.Stage(value.Command, value.Exit, scratch, limits.Report, limits.Report));
            var plain = new List<CoverageStagedFile>();
            foreach (var (path, bytes) in files)
            {
                string stage = Path.Combine(scratch, "file-" + plain.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllBytes(stage, bytes);
                plain.Add(new(path, stage, bytes.LongLength, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
            }
            CoverageStagedArtifact[] artifacts = [.. report == null ? [] : new[] { report }, .. evidence == null ? [] : new[] { evidence }];
            if (fault is { } injected) CoveragePublisher.PublishForTests(plain, artifacts, protectedInputs, injected);
            else CoveragePublisher.Publish(plain, artifacts, protectedInputs);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Output staging refused.", error);
        }
        finally
        {
            report?.Dispose();
            if (scratch != null && Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }
}
