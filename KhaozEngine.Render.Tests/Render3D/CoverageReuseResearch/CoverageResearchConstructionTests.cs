using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>Attributes a bounded external fixture's construction refusal to its unchanged reference phase.
/// Intermediate positions are diagnostics, never a published surface or a geometric certificate.</summary>
public sealed class CoverageResearchConstructionTests
{
    [CoverageResearchFact]
    public void External_fixture_construction_phases_match_the_oracle()
    {
        var manifest = CoverageResearchManifest.Load(Environment.GetEnvironmentVariable(CoverageResearchManifest.Variable)!);
        var fixture = CoverageResearchFixture.Load(manifest);
        var topology = fixture.Topology;
        var skin = new CoverageReferenceSkin(topology.Source, fixture.InverseBind.Length);
        var oracle = new CoverageOracle(topology, fixture.InverseBind, fixture.Commands.Length);
        var rows = new List<object>();
        foreach (var anchor in fixture.Anchors)
        {
            var frame = anchor.Current;
            var commands = fixture.Poses(frame);
            var scratch = new CoverageReferenceScratch(topology, fixture.Commands.Length);
            var posed = new ModelVertex[topology.SourceVertexCount];
            Matrix4x4[] composed = frame.PaletteMatrices.Select((p, i) => CoverageReferenceSkinning.Compose(p, fixture.InverseBind[i])).ToArray();
            Assert.True(CoverageReferenceSkinning.TrySkin(scratch.Arithmetic, skin, topology.Source, composed, posed, out _));
            bool prepared = CoverageReferenceFrames.TryPrepare(topology, frame.ModelMatrix, commands, scratch, out var failure);
            bool interpolated = prepared && CoverageReferenceConstructor.TryInterpolate(topology, posed, scratch, out failure);
            bool conformed = interpolated && CoverageReferenceConstructor.TryConformGroups(topology, commands, scratch, out failure);
            bool corrected = conformed && CoverageReferenceConstructor.TryCorrectAndTransition(topology, commands, scratch, out failure);
            bool attributes = corrected && CoverageReferenceNormals.TryUpdate(topology, scratch, out failure);
            oracle.TryCheck(frame.PaletteMatrices, frame.ModelMatrix, commands, out _, out var refused);
            Assert.Equal(attributes, oracle.LastConstructedVertices.Length == topology.VertexCount);
            if (!attributes) Assert.Equal(refused.Failure.Code, failure.Code);
            rows.Add(new
            {
                anchor.Key,
                prepared,
                interpolated,
                conformed,
                corrected,
                attributes,
                failure = failure.Code.ToString(),
                intermediateAfterCorrection = corrected
                    ? scratch.After.Select(v => new[] { v.Position.X, v.Position.Y, v.Position.Z }).ToArray() : null,
            });
        }
        File.WriteAllText(manifest.Output + ".construction.json", JsonSerializer.Serialize(new { anchors = rows }));
    }
}
