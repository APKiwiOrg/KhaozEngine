using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KhaozEngine.Render3D;

namespace KhaozEngine.Tools.Coverage;

public static class Program
{
    /// <summary>0 complete accepted work, 1 validation/output failure, 2 malformed input.</summary>
    public static int Main(string[] args) => Run(args, CoverageBuildInputJson.MaxBytes);

    internal static int Run(string[] args, int evidenceByteLimit) => Run(args, evidenceByteLimit, null);

    /// <summary>Runs with one synthetic publication fault, through the same staging and publisher as production.</summary>
    internal static int RunForTests(string[] args, int artifactByteLimit, CoveragePublicationFault fault) => Run(args, artifactByteLimit, fault);

    static int Run(string[] args, int evidenceByteLimit, CoveragePublicationFault? fault)
    {
        var diagnostic = new CoverageDiagnosticRun();
        List<string> protectedInputs = [], acceptedOutputs = [];
        int exit = RunAccepted(args, evidenceByteLimit, fault, diagnostic, protectedInputs, acceptedOutputs);
        // A failed transaction that already carried the report leaves the previous report in place.
        if (exit == 1 && diagnostic.Path != null && diagnostic.Input != null && !diagnostic.PublicationAttempted)
        {
            // A refusal replaces only diagnostics, so the accepted table and evidence are protected like inputs.
            try { Publish([], null, (diagnostic, args[0], exit), [.. protectedInputs, .. acceptedOutputs], evidenceByteLimit, fault); }
            catch (CoverageBuildValidationException error) { Console.Error.WriteLine(error.Reason); return 1; }
        }
        return exit;
    }

    static int RunAccepted(string[] args, int evidenceByteLimit, CoveragePublicationFault? fault, CoverageDiagnosticRun diagnostic,
        List<string> protectedInputs, List<string> acceptedOutputs)
    {
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
            var input = CoverageBuildInput.Read(manifest);
            if (diagnostic.Path != null && (input.SourcePaths.Any(p => CoverageDiagnosticName.Unsupported(p, windows))
                || CoverageDiagnosticName.Collides(diagnostic.Path, input.SourcePaths))) return 2;
            diagnostic.Input = CoverageCampaign.FromSnapshot(input);
            if (input.SourcePaths.Any(p => Reaches(CoveragePathIdentity.TryOf(p))))
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputAlias, "Outputs cannot overwrite checked inputs.");
            protectedInputs.AddRange([manifest, .. input.SourcePaths]);
            if (!verifiesTable) acceptedOutputs.Add(tablePath);
            else protectedInputs.Add(tablePath);
            acceptedOutputs.Add(evidencePath);
            // An interrupted earlier publication is finished or undone before any of its outputs is read or replaced.
            CoveragePublisher.Recover(evidencePath);
            if (diagnostic.Path != null) CoveragePublisher.Recover(diagnostic.Path);
            CoverageEvidenceManifest evidence;
            byte[]? tableBytes = null;
            CoverageRecessionTable? suppliedTable = null;
            if (args[0] == "build")
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
            if (args[0] == "verify")
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
            byte[] evidenceBytes = evidence.Write(evidenceByteLimit);
            if (File.Exists(evidencePath)) Resume(evidencePath, evidenceBytes, evidenceByteLimit, diagnostic);
            (string Path, byte[] Bytes)[] outputs = tableBytes == null ? [] : [(tablePath, tableBytes)];
            diagnostic.PublicationAttempted = diagnostic.Path != null;
            Publish(outputs, (evidencePath, evidence, evidenceBytes), diagnostic.Path == null ? null : (diagnostic, args[0], 0),
                protectedInputs, evidenceByteLimit, fault);
            return 0;
        }
        catch (CoverageVerificationException error)
        {
            diagnostic.Refusal = error.Reason.ToString();
            Console.Error.WriteLine(JsonSerializer.Serialize(new
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
        catch (CoverageToolStateException error) { diagnostic.Refusal = error.Reason.ToString(); Console.Error.WriteLine(error.Reason); return 1; }
        catch (CoverageBuildValidationException error) { diagnostic.Refusal = error.Reason.ToString(); Console.Error.WriteLine(error.Reason); return 1; }
        catch (CoverageBuildDataException error) { Console.Error.WriteLine($"{error.Reason} {error.Path}"); return 2; }
        catch (MalformedDataException error) { Console.Error.WriteLine(error.GetType().Name); return 2; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Console.Error.WriteLine(error.GetType().Name); return 2; }
    }

    /// <summary>Opens the existing evidence root through the strict reader, so a set is checked part by part, records
    /// the exact root bytes read for diagnostics, then compares every logical record with the current work.</summary>
    static void Resume(string evidencePath, byte[] current, int rootByteLimit, CoverageDiagnosticRun diagnostic)
    {
        using var existing = CoverageArtifactReader.Open(evidencePath, Path.GetTempPath(), rootByteLimit);
        diagnostic.ResumedEvidenceSha256 = existing.RootSha256;
        using var actual = existing.OpenPayload();
        using var expected = new MemoryStream(current, writable: false);
        CoverageLogicalDocumentReader.Compare(actual, expected, CoverageArtifactKind.Evidence);
    }

    /// <summary>Stages outputs and hands them to the common publisher. The evidence root is published last as the commit
    /// point. It stays one legacy file because its bytes already fit the evidence bound. The diagnostic report is staged
    /// complete, as one file or as a set split at <paramref name="artifactByteLimit"/>.</summary>
    static void Publish((string Path, byte[] Bytes)[] files, (string Path, CoverageEvidenceManifest Manifest, byte[] Bytes)? evidence,
        (CoverageDiagnosticRun Run, string Command, int Exit)? diagnostics, IReadOnlyList<string> protectedInputs, int artifactByteLimit,
        CoveragePublicationFault? fault)
    {
        string? scratch = null;
        var staged = new List<CoverageStagedArtifact>();
        try
        {
            scratch = CoverageArtifactSet.Scratch(Path.GetTempPath());
            var plain = new List<CoverageStagedFile>();
            foreach (var (path, bytes) in files)
            {
                string stage = Path.Combine(scratch, "file-" + plain.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                File.WriteAllBytes(stage, bytes);
                plain.Add(new(path, stage, bytes.LongLength, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))));
            }
            if (diagnostics is { } report)
                staged.Add(report.Run.Stage(report.Command, report.Exit, scratch, artifactByteLimit, artifactByteLimit));
            if (evidence is { } value)
                staged.Add(CoverageArtifactWriter.Stage(value.Path, value.Manifest.CreateHeader(), s => s.Write(value.Bytes), scratch));
            if (fault is { } injected) CoveragePublisher.PublishForTests(plain, staged, protectedInputs, injected);
            else CoveragePublisher.Publish(plain, staged, protectedInputs);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Output staging refused.", error);
        }
        catch (CoverageBuildDataException error) when (error.Reason == CoverageBuildInputRefusal.CapacityExceeded)
        {
            // An output whose index cannot name every part is an output failure, refused before anything is replaced.
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Output exceeds its artifact bound.", error);
        }
        finally
        {
            foreach (var artifact in staged) artifact.Dispose();
            if (scratch != null && Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }
}
