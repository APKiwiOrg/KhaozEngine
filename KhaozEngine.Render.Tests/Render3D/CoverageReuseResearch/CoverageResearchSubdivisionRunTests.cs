using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.CoverageReuseResearch;

/// <summary>Measures externally authored host subdivisions on the original posed surface. This is a topology study,
/// not an authored skin or an oracle admission claim for the diagnostic GLB's interpolated skin weights.</summary>
public sealed class CoverageResearchSubdivisionRunTests
{
    sealed record Configuration(CoverageResearchFile SourceManifest, CoverageResearchFile Recipes);
    sealed record Recipes(CoverageResearchSubdivisionVertex[] Vertices);
    sealed record Run(CoverageResearchFile Manifest, CoverageResearchFile Subdivision);

    [CoverageResearchFact]
    public void External_subdivision_uses_the_unchanged_constructor_and_certifier_on_the_original_posed_surface()
    {
        string? configurationPath = Environment.GetEnvironmentVariable("KE_COVERAGE_RESEARCH_SUBDIVISION");
        if (string.IsNullOrEmpty(configurationPath)) return;
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var document = JsonDocument.Parse(File.ReadAllBytes(configurationPath));
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            var runs = JsonSerializer.Deserialize<Run[]>(document, options)!;
            Assert.NotEmpty(runs);
            foreach (var run in runs)
            {
                run.Manifest.Read();
                run.Subdivision.Read();
                Measure(run.Manifest.Path, run.Subdivision.Path);
            }
            return;
        }
        Measure(Environment.GetEnvironmentVariable(CoverageResearchManifest.Variable)!, configurationPath);
    }

    static void Measure(string manifestPath, string configurationPath)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllBytes(configurationPath), options)!;
        configuration.SourceManifest.Read();
        var source = CoverageResearchFixture.Load(CoverageResearchManifest.Load(configuration.SourceManifest.Path));
        var recipes = JsonSerializer.Deserialize<Recipes>(configuration.Recipes.Read(), options)!.Vertices;
        var manifest = CoverageResearchManifest.Load(manifestPath);
        var target = CoverageResearchFixture.Load(manifest);
        Assert.Equal(target.Topology.SourceVertexCount, recipes.Length);
        Assert.Equal(source.Anchors.Select(a => a.Key), target.Anchors.Select(a => a.Key));
        var sourceSkin = new CoverageReferenceSkin(source.Topology.Source, source.InverseBind.Length);
        var targetSkin = new CoverageReferenceSkin(target.Topology.Source, target.InverseBind.Length);
        var sourcePosed = new ModelVertex[source.Topology.SourceVertexCount];
        var targetPosed = new ModelVertex[target.Topology.SourceVertexCount];
        var final = new ModelVertex[target.Topology.VertexCount];
        var scratch = new CoverageReferenceScratch(target.Topology, target.Commands.Length);
        var certification = new CoverageScratch(target.Topology, target.Commands.Length);
        var surfaces = new CoverageResearchSurfaces(target.Topology);
        var rows = new List<object>();
        var intermediates = new List<object>();
        for (int index = 0; index < target.Anchors.Length; index++)
        {
            File.AppendAllText(manifest.Output + ".progress", $"{index + 1}/{target.Anchors.Length}\n");
            var anchor = target.Anchors[index];
            var original = source.Anchors[index];
            Assert.Equal(original.Current.Model, anchor.Current.Model);
            Assert.Equal(original.Current.Palette.SelectMany(p => p), anchor.Current.Palette.SelectMany(p => p));
            var frame = anchor.Current;
            scratch.Arithmetic.Reset();
            scratch.UniformArithmetic.Reset();
            Skin(source, sourceSkin, original.Current, sourcePosed);
            Skin(target, targetSkin, frame, targetPosed);
            for (int v = 0; v < recipes.Length; v++)
            {
                var recipe = recipes[v];
                if (recipe.OldVertex is int originalVertex)
                {
                    targetPosed[v].Position = sourcePosed[originalVertex].Position;
                    targetPosed[v].Normal = sourcePosed[originalVertex].Normal;
                }
                else
                {
                    var triangle = source.Topology.Source.Triangles[recipe.OldTriangle!.Value];
                    var a = sourcePosed[triangle.A];
                    var b = sourcePosed[triangle.B];
                    var c = sourcePosed[triangle.C];
                    targetPosed[v].Position = CoverageResearchSubdivision.Interpolate(a.Position, b.Position, c.Position, recipe.Barycentric!);
                    targetPosed[v].Normal = Vector3.Normalize(CoverageResearchSubdivision.Interpolate(a.Normal, b.Normal, c.Normal, recipe.Barycentric!));
                }
            }
            foreach (var alias in target.Topology.SourceAliases)
                targetPosed[alias.SourceVertex].Position = targetPosed[alias.Representative].Position;
            var commands = target.Poses(frame);
            bool constructed = CoverageReferenceConstructor.TryConstruct(target.Topology, targetPosed, frame.ModelMatrix,
                commands, scratch, final, out var failure);
            if (!constructed && failure.Code == CoverageFailureCode.FoldedPatch)
                intermediates.Add(new { anchor.Key, phase = Site(scratch.Arithmetic, "phase").ToString(), element = (int)Site(scratch.Arithmetic, "element"), after = scratch.After.Select(v => new[] { v.Position.X, v.Position.Y, v.Position.Z }).ToArray() });
            string stage = constructed ? "Certification" : "Construction";
            bool accepted = false;
            CoverageGeometryCertificate certificate = default;
            if (constructed)
                accepted = CoverageCertification.ValidateGeometry(target.Topology, scratch.Before, final, frame.ModelMatrix,
                    commands, certification, out certificate, out failure);
            int mismatches = 0;
            double displacement = 0;
            if (constructed)
                for (int v = 0; v < final.Length; v++)
                {
                    var vertex = target.Topology.Vertices[v];
                    if (vertex.RegionIndex < 0 || vertex.Strength <= 0)
                        if (final[v].Position != scratch.Before[v].Position) mismatches++;
                    displacement = Math.Max(displacement, Vector3.Distance(final[v].Position, scratch.Before[v].Position));
                }
            Assert.Equal(0, mismatches);
            surfaces.Add(anchor.Key, scratch.Before, final, constructed, scratch, scratch.ActiveCommands > 0);
            rows.Add(new
            {
                anchor.Key,
                Accepted = accepted,
                Stage = accepted ? "None" : stage,
                Code = accepted ? "None" : failure.Code.ToString(),
                Constructed = constructed,
                certificate.FullCoreClearance,
                certificate.MinimumCoreClearance,
                MaximumDisplacement = displacement,
                ProtectedCopyMismatches = mismatches,
            });
        }
        var topology = new CoverageResearchDiagnostics(target).Topology();
        File.WriteAllText(manifest.Output, JsonSerializer.Serialize(new { topology, anchors = rows },
            new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
        surfaces.Write(manifest.Output);
        File.WriteAllText(manifest.Output + ".construction.json", JsonSerializer.Serialize(intermediates));
    }

    static object Site(CoverageReferenceArithmetic arithmetic, string name) =>
        typeof(CoverageReferenceArithmetic).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(arithmetic)
            ?? throw new InvalidOperationException($"Missing research arithmetic site {name}.");

    static void Skin(CoverageResearchFixture fixture, CoverageReferenceSkin skin, CoverageResearchFrame frame, ModelVertex[] posed)
    {
        var palette = frame.PaletteMatrices;
        var composed = fixture.InverseBind.Select((b, i) => CoverageReferenceSkinning.Compose(palette[i], b)).ToArray();
        Assert.True(CoverageReferenceSkinning.TrySkin(new CoverageReferenceArithmetic(), skin, fixture.Topology.Source, composed, posed, out _));
    }
}
