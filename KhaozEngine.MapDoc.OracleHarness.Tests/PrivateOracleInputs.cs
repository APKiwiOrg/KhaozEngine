using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed class PrivateOracleInputException(string message) : Exception(message);

internal sealed class PrivateOracleInputs
{
    static readonly string[] Names =
    {
        "KHAOZ_R2_SHIPPED_SOURCE", "KHAOZ_R2_ORACLE_PROVENANCE",
        "KHAOZ_R2_ORACLE_PROVENANCE_SHA256", "KHAOZ_R2_ORACLE_REPORT",
    };

    internal string SourceRoot { get; }
    internal ShippedSourceProvenance Provenance { get; }
    internal string ReportPath { get; }

    PrivateOracleInputs(string sourceRoot, ShippedSourceProvenance provenance, string reportPath)
    {
        SourceRoot = sourceRoot;
        Provenance = provenance;
        ReportPath = reportPath;
    }

    internal static PrivateOracleInputs FromProcess() => FromEnvironment(Environment.GetEnvironmentVariable);

    internal static PrivateOracleInputs FromEnvironment(Func<string, string?> read)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        try
        {
            foreach (string name in Names) values.Add(name, read(name));
        }
        catch (Exception) { throw new PrivateOracleInputException("provenance unreadable"); }
        return Require(values);
    }

    internal static PrivateOracleInputs Require(IReadOnlyDictionary<string, string?> environment)
    {
        try { return RequireCore(environment); }
        catch (PrivateOracleInputException) { throw; }
        catch (Exception) { throw new PrivateOracleInputException("provenance unreadable"); }
    }

    static PrivateOracleInputs RequireCore(IReadOnlyDictionary<string, string?> environment)
    {
        foreach (string name in Names)
            if (!environment.TryGetValue(name, out string? value) || string.IsNullOrWhiteSpace(value))
                throw new PrivateOracleInputException("missing " + name);

        byte[] bytes;
        try { bytes = File.ReadAllBytes(environment[Names[1]]!); }
        catch (Exception) { throw new PrivateOracleInputException("provenance unreadable"); }
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), environment[Names[2]], StringComparison.Ordinal))
            throw new PrivateOracleInputException("provenance digest mismatch");

        ShippedSourceProvenance provenance;
        try { provenance = ShippedSourceProvenance.Parse(bytes); }
        catch (Exception) { throw new PrivateOracleInputException("provenance unreadable"); }

        string reportPath;
        try
        {
            reportPath = PrivateOraclePaths.Canonical(environment[Names[3]]!);
            for (DirectoryInfo? directory = new(Path.GetDirectoryName(reportPath)!); directory is not null; directory = directory.Parent)
            {
                string marker = Path.Combine(directory.FullName, ".git");
                if (File.Exists(marker) || Directory.Exists(marker))
                    throw new PrivateOracleInputException("report path inside a git work tree");
            }
            if (File.Exists(reportPath) || Directory.Exists(reportPath) || new FileInfo(reportPath).LinkTarget is not null)
                throw new PrivateOracleInputException("report path already exists");
        }
        catch (PrivateOracleInputException) { throw; }
        catch (Exception) { throw new PrivateOracleInputException("provenance unreadable"); }

        string sourceRoot;
        try { sourceRoot = PrivateOraclePaths.Canonical(environment[Names[0]]!); }
        catch (Exception) { throw new PrivateOracleInputException("source verification failed: 1 files"); }
        int mismatches = provenance.Verify(sourceRoot);
        if (mismatches != 0) throw new PrivateOracleInputException($"source verification failed: {mismatches} files");
        return new(sourceRoot, provenance, reportPath);
    }
}

internal static class PrivateOraclePaths
{
    internal static string Canonical(string path)
    {
        string full = Path.GetFullPath(path);
        string current = Path.GetPathRoot(full)!;
        foreach (string part in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget is not null)
                next = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("unresolved path link");
            current = next;
        }
        return current;
    }
}
