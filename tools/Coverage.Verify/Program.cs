using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            if (args.Length != 7 || args[0] is not ("build" or "verify-samples")) return 2;
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
            if (args[0] == "build")
            {
                var result = CoverageTableBuilder.Build(input);
                evidence = result.Evidence;
                tableBytes = CoverageRecessionTableBinary.Write(result.Table);
                CoverageBuildInputJson.CheckByteCapacity(tableBytes.LongLength, CoverageBuildInputJson.MaxBytes, tablePath);
            }
            else
            {
                var table = CoverageRecessionTableBinary.Read(CoverageBuildInputJson.ReadBytes(tablePath), tablePath);
                evidence = CoverageSampleVerifier.Verify(input, table);
            }
            if (!evidence.Complete) return 1;
            byte[] evidenceBytes = evidence.Write(evidenceByteLimit);
            if (File.Exists(evidencePath)) evidence.ValidateResume(CoverageBuildInputJson.ReadBytes(evidencePath, evidenceByteLimit), evidenceBytes);
            Publish(tableBytes == null ? [(evidencePath, evidenceBytes)] : [(tablePath, tableBytes), (evidencePath, evidenceBytes)]);
            return 0;
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
