using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.Tests.MapDoc;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
internal static class PrivateOracleFixtures
{
    internal const UnixFileMode SecureDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    internal static void InSecureTemp(Action<string> body)
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("private oracle fixtures require Unix");
        string root = Path.Combine(Path.GetTempPath(), "ke-private-oracle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root, SecureDirectoryMode);
        try { body(root); }
        finally { Directory.Delete(root, recursive: true); }
    }

    internal static Dictionary<string, string?> SyntheticEnvironment(string root, string secret)
    {
        string source = Path.Combine(root, secret, "src");
        Directory.CreateDirectory(source);
        string file = Path.Combine(source, "region.txt");
        File.WriteAllText(file, "synthetic");
        var provenance = new ShippedSourceProvenance
        {
            SourceRepository = "synthetic",
            Tag = "synthetic",
            Commit = new string('1', 40),
            SourceEnginePin = "20.27.1",
            ExtractionPathspec = new[] { "region.txt" },
            Paths = new[] { new ShippedSourcePath("region.txt", AssertFixtures.Sha256(file)) },
            AggregateRule = ShippedSourceProvenance.AggregateDefinition,
            Units = JsonSerializer.SerializeToElement("synthetic"),
            RowOrientation = "synthetic",
            Comparison = JsonSerializer.SerializeToElement("exact"),
            OracleEquivalence = JsonSerializer.SerializeToElement("synthetic"),
        };
        provenance = provenance with { AggregateSha256 = provenance.ComputeAggregate() };
        string manifest = Path.Combine(root, secret, "provenance.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(provenance, ShippedSourceProvenance.JsonOptions));
        string output = Path.Combine(root, "out");
        Directory.CreateDirectory(output, SecureDirectoryMode);
        return new(StringComparer.Ordinal)
        {
            ["KHAOZ_R2_SHIPPED_SOURCE"] = source,
            ["KHAOZ_R2_ORACLE_PROVENANCE"] = manifest,
            ["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(manifest),
            ["KHAOZ_R2_ORACLE_REPORT"] = Path.Combine(output, "report.json"),
        };
    }

    internal static void Throw(string fault, string sourceRoot)
    {
        string message = "at " + sourceRoot;
        throw fault switch
        {
            "filesystem" => new FileNotFoundException(message),
            "json" => new JsonException(message),
            "loader" => new InvalidDataException(message),
            "arithmetic" => new OverflowException(message),
            _ => new ArgumentException("unknown synthetic fault"),
        };
    }
}
