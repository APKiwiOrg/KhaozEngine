using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Gpu;

internal sealed class CoverageCarryCertificateMeasureFactAttribute : FactAttribute
{
    public CoverageCarryCertificateMeasureFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KE_COVERAGE_CERTIFICATE_RESULTS")))
            Skip = "Set KE_COVERAGE_CERTIFICATE_RESULTS for the single finite offline F0 measurement.";
    }
}

public sealed class CoverageCarryCertificateMeasurement(ITestOutputHelper output)
{
    [CoverageCarryCertificateMeasureFact]
    public void Finite_F0_domains_and_lifetimes()
    {
        string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("KE_COVERAGE_CERTIFICATE_RESULTS")!);
        if (!directory.StartsWith("/tmp/khaozengine-", StringComparison.Ordinal))
            throw new InvalidOperationException("Research evidence belongs under /tmp/khaozengine-<topic>.");
        Directory.CreateDirectory(directory);
        var wall = Stopwatch.StartNew(); long allocated = GC.GetTotalAllocatedBytes();
        var fixture = new CoverageCarryFixture();
        var builds = new[] { CoverageCarryCertificateBuilder.Build(fixture, 0), CoverageCarryCertificateBuilder.Build(fixture, 1) };
        Assert.All(builds, b => Assert.NotNull(b.Cell));
        var cells = builds.Select(b => b.Cell!).ToArray();
        var candidates = builds.SelectMany(b => b.Candidates).ToArray();
        var replay = cells.SelectMany(c => new[] { CoverageCarryReplay.Run(c, 30), CoverageCarryReplay.Run(c, 60) })
            .Concat(new[] { CoverageCarryReplay.Alternating(cells, 30), CoverageCarryReplay.Alternating(cells, 60) }).ToArray();
        foreach (var c in candidates) File.WriteAllBytes(Path.Combine(directory, $"{c.Motion}-reserve-{c.ReserveWorld * 1000000:F0}um-proof.json"), CoverageCarryCellBinding.Proof(c));
        var domains = candidates.Select(c => new
        {
            c.Motion,
            c.Radius,
            c.ReserveWorld,
            c.Binding,
            c.DomainDigest,
            dimension = 6,
            selectedForReplay = cells.Contains(c),
            proofBytes = CoverageCarryCellBinding.ProofBytes(c),
            planes = c.Planes.Length,
            check = CoverageCarryCertificateChecker.Check(c),
        }).ToArray();
        wall.Stop(); using var process = Process.GetCurrentProcess();
        var report = new
        {
            schema = "public-f0-e2/1",
            fixture = "coverage-typical-gpu/1",
            privateData = false,
            sourceVertices = fixture.Topology.SourceVertexCount,
            vertices = fixture.Topology.VertexCount,
            triangles = fixture.Topology.TriangleCount,
            wallMilliseconds = wall.Elapsed.TotalMilliseconds,
            allocatedBytes = GC.GetTotalAllocatedBytes() - allocated,
            peakWorkingSet = process.PeakWorkingSet64 > 0 ? process.PeakWorkingSet64 : (long?)null,
            domains,
            attempts = builds.SelectMany(b => b.Attempts),
            replay,
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            IncludeFields = true,
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        });
        File.WriteAllText(Path.Combine(directory, "e2-f0.json"), json);
        foreach (var d in domains) output.WriteLine($"{d.Motion}: radius={d.Radius:R}, reserve={d.ReserveWorld:R}, bytes={d.proofBytes}, {d.check}");
        output.WriteLine($"Evidence {directory}, {wall.Elapsed.TotalSeconds:F3} seconds");
    }
}
