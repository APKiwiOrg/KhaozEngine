using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class SurfaceHeightCallSiteTests
{
    [Fact]
    public void ProductionCallSitesAreClassified()
    {
        string[] expected =
        {
            "KhaozEngine.Movement/GroundNavigation.cs:Resolve:RestingHeightAt",
            // Drop candidate heights, cost and endpoints all use certified physical feet anchors.
            "KhaozEngine.Movement/ContactDropCandidates.cs:Enumerate:RestingHeightAt",
            "KhaozEngine.Movement/ContactDropCandidates.cs:Enumerate:RestingHeightAt",
            "KhaozEngine.Movement/ContactDropCandidates.cs:Measure:RestingHeightAt",
            "KhaozEngine.Movement/ContactDropCandidates.cs:Measure:RestingHeightAt",
            "KhaozEngine.Movement/ContactDropProof.cs:Feet:RestingHeightAt",
            // Terrain candidates and landing bands use certified physical feet anchors too.
            "KhaozEngine.Movement/ContactTerrainDropCandidates.cs:Enumerate:RestingHeightAt",
            "KhaozEngine.Movement/ContactTerrainDropCertificate.cs:TargetInBand:RestingHeightAt",
            "KhaozEngine.Movement/TerrainCandidateContract.cs:Validate:RestingHeightAt",
            "KhaozEngine.Movement/TerrainCandidateContract.cs:Validate:RestingHeightAt",
            "KhaozEngine.Navigation/GridPathPlanner.Links.cs:LinkCost:RestingHeightAt",
            "KhaozEngine.Navigation/GridPathPlanner.Links.cs:LinkCost:RestingHeightAt",
            "KhaozEngine.Navigation/PathFollower.Drop.cs:TryDrop:RestingHeightAt",
            "KhaozEngine.Navigation/PathFollower.Drop.cs:TryDrop:RestingHeightAt",
            "KhaozEngine.Movement/RouteStraightener.cs:TryFeet:RestingHeightAt",
            // Pursuit route membership seats the actual capsule on certified physical feet, not raw surfaces.
            "KhaozEngine.Movement/GroundPursuitRoute.cs:Validate:RestingHeightAt",
            "KhaozEngine.Movement/PhysicsNavBake.Profiles.cs:Feet:RestingHeightAt",
            "KhaozEngine.Navigation/GridPathPlanner.Region.cs:CellFeet:RestingHeightAt",
            "KhaozEngine.Navigation/NavSpace.cs:LayerAt:RestingHeightAt",
            "KhaozEngine.Movement/PhysicsNavBake.Profiles.cs:IsFloatNode:SurfaceHeightAt",
            // Match original raw source slots before either contact path substitutes resting heights.
            "KhaozEngine.Movement/ContactNavigationSources.cs:BindLayers:SurfaceHeightAt",
            // #1697. Terrain bindings retain raw source identity, including constructor reads.
            "KhaozEngine.Movement/ContactTerrainProfileEvidence.cs:.ctor:SurfaceHeightAt",
            // Stored Stair shape validation preserves the raw-height recipe.
            "KhaozEngine.Movement/TerrainCandidateContract.cs:Validate:SurfaceHeightAt",
            "KhaozEngine.Movement/TerrainCandidateContract.cs:Validate:SurfaceHeightAt",
            "KhaozEngine.Movement/GroundNavigationBake.Writer.cs:WriteProfile:SurfaceHeightAt",
            // Format 2 appends physical feet separately, preserving the raw topology section.
            "KhaozEngine.Movement/GroundNavigationBake.Writer.cs:WriteRestingHeights:RestingHeightAt",
            "KhaozEngine.Navigation/NavLayerLinks.cs:EnumerateCore:SurfaceHeightAt",
            "KhaozEngine.Navigation/NavLayerLinks.cs:EnumerateCore:SurfaceHeightAt",
            "KhaozEngine.Navigation/NavHopLinks.cs:Generate:SurfaceHeightAt",
            "KhaozEngine.Navigation/NavHopLinks.cs:Generate:SurfaceHeightAt",
        };
        var actual = new List<string>();
        int productionFiles = 0;
        string root = RepositoryRoot();
        foreach (string project in new[] { "KhaozEngine.Navigation", "KhaozEngine.Movement" })
        {
            string directory = Path.Combine(root, project);
            foreach (string path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Split('/').Any(segment => segment is "bin" or "obj")) continue;
                string name = Path.GetFileName(path);
                if (name == "NavGrid.cs" || name.StartsWith("NavGrid.", StringComparison.Ordinal)) continue;

                productionFiles++;
                actual.AddRange(ScanCallSites(File.ReadAllText(path), relative).Select(call => call.Site));
            }
        }

        Assert.True(productionFiles > 0, "The height call-site sweep found no production source files.");
        Assert.NotEmpty(actual);
        // Keep duplicates so both raw reads in each link generator are pinned.
        Assert.Equal(expected.OrderBy(site => site, StringComparer.Ordinal),
            actual.OrderBy(site => site, StringComparer.Ordinal));
    }

    [Fact]
    public void ScannerFindsCallAfterBlockCommentMarkerInString()
    {
        const string source = """
            internal static class HeightReaderProbe
            {
                public static float? Read(KhaozEngine.Navigation.NavGrid grid)
                {
                    _ = "/*";
                    return grid.SurfaceHeightAt(0, 0);
                }
            }
            """;

        var call = Assert.Single(ScanCallSites(source, "HeightReaderProbe.cs"));
        Assert.Equal("HeightReaderProbe", call.Type);
        Assert.Equal("HeightReaderProbe.cs:Read:SurfaceHeightAt", call.Site);
    }

    [Fact]
    public void ScannerFindsCallOnSameLineAsLineCommentMarkerInString()
    {
        const string source = """
            internal static class HeightReaderProbe
            {
                public static float? Read(KhaozEngine.Navigation.NavGrid grid)
                {
                    _ = "//"; return grid.SurfaceHeightAt(0, 0);
                }
            }
            """;

        var call = Assert.Single(ScanCallSites(source, "HeightReaderProbe.cs"));
        Assert.Equal("HeightReaderProbe", call.Type);
        Assert.Equal("HeightReaderProbe.cs:Read:SurfaceHeightAt", call.Site);
    }

    static IEnumerable<(string Type, string Site)> ScanCallSites(string source, string relative)
    {
        SyntaxNode root = CSharpSyntaxTree.ParseText(source,
            new CSharpParseOptions(LanguageVersion.Latest), path: relative).GetRoot();
        Assert.DoesNotContain(root.GetDiagnostics(), diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            SimpleNameSyntax? name = call.Expression switch
            {
                SimpleNameSyntax direct => direct,
                MemberAccessExpressionSyntax member => member.Name,
                MemberBindingExpressionSyntax member => member.Name,
                _ => null,
            };
            string? reader = name?.Identifier.ValueText;
            if (reader is not ("SurfaceHeightAt" or "RestingHeightAt")) continue;

            BaseTypeDeclarationSyntax? type = call.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
            BaseMethodDeclarationSyntax? declaration = call.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            Assert.NotNull(type);
            Assert.NotNull(declaration);
            string owner = declaration switch
            {
                MethodDeclarationSyntax method => method.Identifier.ValueText,
                ConstructorDeclarationSyntax => ".ctor",
                _ => throw new InvalidOperationException("The height call-site sweep needs a method or constructor."),
            };
            yield return (type.Identifier.ValueText, $"{relative}:{owner}:{reader}");
        }
    }

    static string RepositoryRoot()
    {
        DirectoryInfo? directory = new FileInfo(typeof(SurfaceHeightCallSiteTests).Assembly.Location).Directory;
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KhaozEngine.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("The call-site sweep needs a checkout containing KhaozEngine.slnx above the test assembly.");
    }
}
