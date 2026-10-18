using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceOutputTests
{
    const string WriterGate = "KE_WRITE_COVERAGE_REFERENCE_HASHES";
    const string DirectoryName = "coverage-reference-v1";
    const string ManifestName = "outputs.sha256";

    [Fact]
    public void Every_fixture_reference_output_matches_the_hash_manifest()
    {
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", DirectoryName, ManifestName))
            .Select(line => line.Split(' ', 2)).Select(parts => (Name: parts[1], Hash: parts[0])).ToArray();
        var actual = CoverageReferenceOutputs.ComputeAll().ToArray();
        Assert.Equal(144, expected.Length);
        Assert.Equal(expected.Select(e => e.Name), actual.Select(a => a.Name));
        Assert.All(actual, a => Assert.True(a.Failure.IsSuccess, $"{a.Name}: {a.Failure}"));
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i].Hash == actual[i].Hash,
                $"First differing reference output: {actual[i].Name}, expected {expected[i].Hash}, actual {actual[i].Hash}.");
        Assert.Equal(expected.Select(e => e.Hash), actual.Select(a => a.Hash));
    }

    [Fact]
    public void Reference_output_writer_is_disabled_without_explicit_environment_gate()
    {
        string elsewhere = Path.Combine(Path.GetTempPath(), $"coverage-reference-{Guid.NewGuid():N}");
        foreach (string? gate in new[] { null, "", "0", "true", "yes", " 1", "1 " })
            Assert.False(TryWriteManifest(gate, elsewhere));
        Assert.False(Directory.Exists(elsewhere));
        if (Environment.GetEnvironmentVariable(WriterGate) == "1") return;
        string path = Path.Combine(SourceFixtureDirectory(), ManifestName);
        byte[] before = File.ReadAllBytes(path);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        Write_reference_output_manifest_when_explicitly_requested();
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void Write_reference_output_manifest_when_explicitly_requested()
    {
        TryWriteManifest(Environment.GetEnvironmentVariable(WriterGate), SourceFixtureDirectory());
    }

    static bool TryWriteManifest(string? gate, string directory)
    {
        if (gate != "1") return false;
        var rows = CoverageReferenceOutputs.ComputeAll().ToArray();
        Assert.Equal(144, rows.Length);
        Assert.All(rows, row => Assert.True(row.Failure.IsSuccess, $"{row.Name}: {row.Failure}"));
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, ManifestName), rows.Select(row => $"{row.Hash} {row.Name}"));
        return true;
    }

    static string SourceFixtureDirectory([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "Fixtures", DirectoryName));
}

/// <summary>Cold reference output bytes, in the archived manifest's ordinal case and then mode order.</summary>
internal static class CoverageReferenceOutputs
{
    internal static string Hash(ReadOnlySpan<ModelVertex> vertices)
    {
        using var bytes = new MemoryStream();
        using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            foreach (var vertex in vertices)
                foreach (float field in CoverageReferenceBits.Fields(vertex)) writer.Write(field);
        return Convert.ToHexStringLower(SHA256.HashData(bytes.ToArray()));
    }

    internal static IEnumerable<(string Name, string Hash, CoverageFailure Failure)> ComputeAll()
    {
        foreach (var c in CoverageReferenceCases.All().OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var scratch = new CoverageReferenceScratch(c.Topology);
            var result = new ModelVertex[c.Topology.VertexCount];
            var capture = CoverageExactCaptureTests.Read(c.Id);
            for (int mode = 0; mode < 2; mode++)
            {
                double[] target = mode == 0 ? [] : capture.ClearanceCoverage.Zip(capture.SlackCoverage, (a, b) => a + b).ToArray();
                CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands,
                    scratch, result, out var failure, target, mode == 0 ? [] : capture.CorrectionClearance,
                    mode == 0 ? [] : capture.CorrectionConversion);
                yield return ($"{c.Id}-{mode}.bin", Hash(result), failure);
            }
        }
    }
}
