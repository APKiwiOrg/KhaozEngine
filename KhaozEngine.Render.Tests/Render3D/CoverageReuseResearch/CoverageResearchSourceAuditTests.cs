using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>Audits the displayed solid even when its proposed directional sheet cannot compile.</summary>
public sealed class CoverageResearchSourceAuditTests
{
    [Fact]
    public void Public_control_sources_are_closed_before_directional_support_validation()
    {
        var geometry = CoverageCostGeometry.Build(CoverageCostFixtureMode.Typical);
        var source = GltfLoader.LoadCoverageSource(geometry.EquipmentGlb(), "source-audit-control");
        var solid = CoverageResearchDiagnostics.Solid(source);
        Assert.Equal(0, solid.OpenEdges);
        Assert.Equal(0, solid.MisorientedEdges);
        Assert.Equal(0, solid.ContactsBetweenComponents);
        foreach (var command in geometry.Commands)
        {
            var ranges = command.InwardSupport.Select(r =>
                new CoverageTriangleRange(r.Mesh, r.Primitive, r.FirstTriangle, r.Count)).ToArray();
            var support = CoverageSupportSurface.Compile(source, ranges, command.CoverageLocal);
            Assert.True(support.InwardOfClosedSolid);
        }
    }

    [CoverageResearchFact]
    public void External_source_solids_are_diagnosed_before_directional_support_validation()
    {
        var manifest = CoverageResearchManifest.Load(Environment.GetEnvironmentVariable(CoverageResearchManifest.Variable)!);
        var settings = manifest.Proposal ?? throw new InvalidDataException("Source audit requires proposal settings.");
        int part = Array.FindIndex(manifest.Parts, p => p.Id == settings.PartId);
        Assert.True(part >= 0);
        var source = GltfLoader.LoadCoverageSource(manifest.Parts[part].Glb.Read(), "source-audit");
        var frame = CoverageResearchAnchors.Matrix(settings.CoverageLocal);
        var ranges = CoverageResearchProposalBuilder.SupportRanges(source, settings.SupportMesh, frame, settings.InwardCosine);
        int[] selected = source.Select(ranges);
        Matrix4x4 inverse = CoverageSupportValidation.InvertFrame(source, frame);
        var projected = source.Positions.ToArray().Select(p => CoverageAffinePoint.Project(p, inverse)).ToArray();
        bool inward = CoverageSolidCertificate.Inward(source, projected, selected);
        string? refusal = null;
        try { CoverageSupportSurface.Compile(source, ranges, frame); }
        catch (InvalidDataException error) { refusal = error.Message; }
        File.WriteAllText(manifest.Output + ".sources.json", JsonSerializer.Serialize(new
        {
            solid = CoverageResearchDiagnostics.Solid(source),
            inwardOfClosedSolid = inward,
            supportTriangles = selected.Length,
            supportRanges = ranges,
            supportCompilationRefusal = refusal,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
