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

    internal static int Run(string[] args, int evidenceByteLimit)
    {
        try
        {
            if (args.Length != 7 || args[0] is not ("build" or "verify-samples" or "verify")) return 2;
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
                if (args[i] is not ("--manifest" or "--table" or "--evidence") || string.IsNullOrWhiteSpace(args[i + 1]) || !options.TryAdd(args[i], Path.GetFullPath(args[i + 1]))) return 2;
            string manifest = options["--manifest"], tablePath = options["--table"], evidencePath = options["--evidence"];
            var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            if (comparer.Equals(manifest, tablePath) || comparer.Equals(manifest, evidencePath) || comparer.Equals(tablePath, evidencePath))
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputAlias, "Output paths must be distinct from inputs and each other.");
            var input = CoverageBuildInput.Read(manifest);
            if (input.SourcePaths.Any(p => comparer.Equals(p, tablePath) || comparer.Equals(p, evidencePath)))
                throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputAlias, "Outputs cannot overwrite checked inputs.");
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
                suppliedTable = CoverageRecessionTableBinary.Read(CoverageBuildInputJson.ReadBytes(tablePath), tablePath);
                evidence = CoverageSampleVerifier.Verify(input, suppliedTable);
            }
            if (args[0] == "verify")
            {
                // Run dense work to completion before choosing a blocking reason. Neither gate substitutes for the other.
                var replay = CoverageReplay.Verify(input, suppliedTable!);
                evidence.IncludeReplay(replay);
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
            if (File.Exists(evidencePath)) evidence.ValidateResume(CoverageBuildInputJson.ReadBytes(evidencePath, evidenceByteLimit), evidenceBytes);
            Publish(tableBytes == null ? [(evidencePath, evidenceBytes)] : [(tablePath, tableBytes), (evidencePath, evidenceBytes)]);
            return 0;
        }
        catch (CoverageVerificationException error)
        {
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
        catch (CoverageToolStateException error) { Console.Error.WriteLine(error.Reason); return 1; }
        catch (CoverageBuildValidationException error) { Console.Error.WriteLine(error.Reason); return 1; }
        catch (CoverageBuildDataException error) { Console.Error.WriteLine($"{error.Reason} {error.Path}"); return 2; }
        catch (MalformedDataException error) { Console.Error.WriteLine(error.GetType().Name); return 2; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Console.Error.WriteLine(error.GetType().Name); return 2; }
    }

    static void Publish((string Path, byte[] Bytes)[] outputs)
    {
        var staged = new List<(string Path, string Temp, string Backup, bool Existed)>();
        int committed = 0;
        try
        {
            foreach (var output in outputs)
            {
                string nonce = Guid.NewGuid().ToString("N");
                var item = (output.Path, Temp: output.Path + "." + nonce + ".tmp", Backup: output.Path + "." + nonce + ".bak", Existed: File.Exists(output.Path));
                staged.Add(item);
                File.WriteAllBytes(item.Temp, output.Bytes);
                if (item.Existed) File.Copy(item.Path, item.Backup);
            }
            foreach (var item in staged) { File.Move(item.Temp, item.Path, true); committed++; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            for (int i = committed - 1; i >= 0; i--)
            {
                var item = staged[i];
                if (item.Existed) File.Move(item.Backup, item.Path, true);
                else File.Delete(item.Path);
            }
            throw new CoverageBuildValidationException(CoverageBuildValidationRefusal.OutputIo, "Output publication refused.", error);
        }
        finally
        {
            foreach (var item in staged)
            {
                if (File.Exists(item.Temp)) File.Delete(item.Temp);
                if (File.Exists(item.Backup)) File.Delete(item.Backup);
            }
        }
    }
}
