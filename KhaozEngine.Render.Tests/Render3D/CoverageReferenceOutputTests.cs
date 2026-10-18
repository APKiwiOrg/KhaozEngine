using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using KhaozEngine.Render3D;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D;

public sealed class CoverageReferenceOutputTests(ITestOutputHelper output)
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

    [Fact]
    public void Fixture_reference_constructions_report_flush_events()
    {
        var expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", DirectoryName, ManifestName))
            .Select(line => line.Split(' ', 2)).ToDictionary(parts => parts[1], parts => parts[0]);
        var rows = CoverageReferenceOutputs.ComputeAll().ToArray();
        Assert.Equal(144, rows.Length);
        foreach (var row in rows)
        {
            output.WriteLine($"{row.Name}: flushes={row.Events}, first={row.FirstSite}, slot={row.FirstSlot}, before={expected[row.Name]}, after={row.Hash}");
            Assert.True(row.Failure.IsSuccess, $"{row.Name}: {row.Failure}");
        }
        Assert.All(rows, row => Assert.True(expected[row.Name] == row.Hash,
            $"{row.Name}: before {expected[row.Name]}, after {row.Hash}, flushes {row.Events}, first {row.FirstSite}"));
    }

    [Fact]
    public void Subnormal_source_attribute_reports_its_first_consuming_operation()
    {
        var c = CoverageReferenceCases.All().Single(c => c.Id == "curved-ordinary-seams-l2-w1");
        var posed = (ModelVertex[])c.Posed.Clone();
        posed[0].Color.X = MathF.ScaleB(1, -130);
        var scratch = new CoverageReferenceScratch(c.Topology);
        var output = new ModelVertex[c.Topology.VertexCount];
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, posed, c.Model, c.Commands, scratch, output, out var failure), failure.ToString());
        Assert.Equal(new CoverageExactnessSite(CoverageExactnessPhase.SourceValidation, 0, CoverageExactnessOperation.FiniteTest,
            BitConverter.SingleToUInt32Bits(posed[0].Color.X)), scratch.Arithmetic.FirstSite);
        Assert.Equal(-1, scratch.Arithmetic.FirstSlot);
        int original = Enumerable.Range(0, c.Topology.VertexCount).First(v => c.Topology.Vertices[v].SourceVertex == 0);
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(output[original].Color.X));
    }

    [Fact]
    public void Tracing_does_not_change_outputs()
    {
        var c = CoverageReferenceCases.All().First();
        var scratch = new CoverageReferenceScratch(c.Topology);
        var output = new ModelVertex[c.Topology.VertexCount];
        scratch.Arithmetic.At(CoverageExactnessPhase.Skinning, 17, 2);
        _ = scratch.Arithmetic.Mul(float.Epsilon, 1);
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, output, out _));
        string hash = CoverageReferenceOutputs.Hash(output);
        var trace = (scratch.Arithmetic.EventCount, scratch.Arithmetic.FirstSite, scratch.Arithmetic.FirstSlot);
        Assert.True(CoverageReferenceConstructor.TryConstruct(c.Topology, c.Posed, c.Model, c.Commands, scratch, output, out _));
        Assert.Equal(hash, CoverageReferenceOutputs.Hash(output));
        Assert.Equal(trace, (scratch.Arithmetic.EventCount, scratch.Arithmetic.FirstSite, scratch.Arithmetic.FirstSlot));
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

    internal static IEnumerable<(string Name, string Hash, CoverageFailure Failure, int Events, CoverageExactnessSite FirstSite, int FirstSlot)> ComputeAll()
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
                yield return ($"{c.Id}-{mode}.bin", Hash(result), failure, scratch.Arithmetic.EventCount, scratch.Arithmetic.FirstSite, scratch.Arithmetic.FirstSlot);
            }
        }
    }
}
