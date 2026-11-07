using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>Skipped unless <c>KE_COVERAGE_RESEARCH_MANIFEST</c> names a research manifest outside the repository.</summary>
public sealed class CoverageResearchFactAttribute : FactAttribute
{
    public CoverageResearchFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(CoverageResearchManifest.Variable)))
            Skip = $"set {CoverageResearchManifest.Variable} to run";
    }
}

/// <summary>Feasibility experiment E0: fixture identities and exact anchor diagnostics. The synthetic control always
/// runs headless. A private fixture runs only from an external manifest and writes only to its external output path.
/// </summary>
public sealed class CoverageResearchE0Tests(ITestOutputHelper output)
{
    internal const string ControlOutputVariable = "KE_COVERAGE_RESEARCH_CONTROL_OUTPUT";

    [Fact]
    public void Synthetic_control_identity_is_reproducible_and_its_anchors_are_diagnosed()
    {
        var (first, fingerprint) = Control();
        var (_, again) = Control();
        Assert.Equal(fingerprint, again);
        var diagnostics = new CoverageResearchDiagnostics(first);
        var topology = diagnostics.Topology();
        Assert.Equal(1_092, topology.SourceVertices);
        Assert.Equal(1_258, topology.FinalVertices);
        Assert.Equal(2_332, topology.FinalTriangles);
        Assert.Equal(new[] { 4, 4 }, topology.Supports.Select(s => s.InwardTriangles));
        var anchors = first.Anchors.Select(diagnostics.Run).ToArray();
        Assert.All(anchors, a => Assert.True(a.Accepted, $"{a.Key}: {a.Stage} {a.Code}"));
        Assert.All(anchors, a => Assert.Equal(0, a.ProtectedCopyMismatches));
        Write(Environment.GetEnvironmentVariable(ControlOutputVariable), first, fingerprint, topology, anchors);
    }

    [CoverageResearchFact]
    public void External_fixture_anchors_are_diagnosed()
    {
        var manifest = CoverageResearchManifest.Load(Environment.GetEnvironmentVariable(CoverageResearchManifest.Variable)!);
        var fixture = CoverageResearchFixture.Load(manifest);
        var diagnostics = new CoverageResearchDiagnostics(fixture);
        var topology = diagnostics.Topology();
        var anchors = fixture.Anchors.Select(diagnostics.Run).ToArray();
        string identity = Hash(string.Join(':', manifest.Fixture, manifest.Host.Sha256, manifest.Attachment.Sha256,
            string.Join(',', manifest.Parts.Select(p => $"{p.Id}={p.Glb.Sha256}")), fixture.HostMapSemanticSha256,
            fixture.CoverageSemanticSha256, manifest.Anchors.Sha256, Fingerprint(fixture.Topology)));
        Write(manifest.Output, fixture, identity, topology, anchors);
        diagnostics.Surfaces.Write(manifest.Output);
        output.WriteLine($"{fixture.Name}: {anchors.Count(a => a.Accepted)} of {anchors.Length} anchors accepted.");
    }

    static (CoverageResearchFixture Fixture, string Fingerprint) Control()
    {
        var geometry = CoverageCostGeometry.Build(CoverageCostFixtureMode.Typical);
        byte[] glb = geometry.EquipmentGlb();
        var equipment = GltfLoader.LoadCoverageSource(glb, "coverage-research-control-equipment");
        var commands = geometry.Commands.Select((c, k) =>
        {
            var ranges = c.InwardSupport.Select(r => new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray();
            return new CoverageResearchCommand(c, CoverageSupportSurface.Compile(equipment, ranges, c.CoverageLocal), 0, k);
        }).ToArray();
        // The cost fixture's two poses. Its plates ride the root, so the part world is the root joint's matrix.
        var anchors = Enumerable.Range(0, 2).Select(pose =>
        {
            var palette = new Matrix4x4[2];
            CoverageCostGeometry.Pose(pose, palette);
            var frame = new CoverageResearchFrame(Flat(Matrix4x4.Identity), palette.Select(Flat).ToArray(), [Flat(palette[0])]);
            return new CoverageResearchAnchor($"control/pose-{pose}", frame, null);
        }).ToArray();
        var fixture = CoverageResearchFixture.Create(CoveragePerfManifest.FixtureVersion, geometry.Refinement,
            [Matrix4x4.Identity, Matrix4x4.Identity], [equipment], commands, anchors, equipment.SourceSha256);
        // The fingerprint the performance scene publishes for this fixture.
        return (fixture, Hash(CoveragePerfManifest.FixtureVersion + ":" + geometry.Topology.Fingerprint + ":" + equipment.SourceSha256));
    }

    static string Fingerprint(CoverageRefinement topology) => CoverageCostGeometry.Observe(topology, 0).Fingerprint;

    static float[] Flat(Matrix4x4 m) =>
        [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];

    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static void Write(string? path, CoverageResearchFixture fixture, string identity, CoverageResearchTopologyReport topology,
        CoverageResearchAnchorReport[] anchors)
    {
        if (string.IsNullOrEmpty(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            fixture = fixture.Name,
            identity,
            hostMapSemanticSha256 = fixture.HostMapSemanticSha256,
            coverageSemanticSha256 = fixture.CoverageSemanticSha256,
            topologyFingerprint = Fingerprint(fixture.Topology),
            proposal = fixture.Proposal,
            topology,
            anchors,
        }, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }
}
