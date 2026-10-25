using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SceneSourceRoster_AttestedAllowanceCountsEmptyInRasterOwners(int allowance)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-wide-raster", json =>
        {
            var profile = LimitsProfile(); var context = Family("foliageContext");
            context["maxChunkDescriptors"] = allowance; profile["foliageContext"] = context; json["limits"] = profile;
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var scope = ShippedSceneInputs.Scope(inputs.Manifest.Windows[0]) with { DrawBounds = new(-1, -10, -5, 34, 10, 1) };
        if (allowance == 1)
        {
            var error = Assert.Throws<MapSceneCapacityException>(() => LegacySceneRecorder.RecordFoliage(inputs, scope));
            Assert.Contains("MaxChunkDescriptors", error.Message);
            Assert.Contains("requested 2", error.Message); Assert.Contains("allowance 1", error.Message);
        }
        else
        {
            var roster = LegacySceneRecorder.RecordFoliage(inputs, scope);
            Assert.Equal(2, roster.Count);
            Assert.NotEmpty(roster[0].Rows); Assert.Empty(roster[1].Rows);
        }
    }

    [Fact]
    public void SceneFullPath_BroadWindowOverSparseRasterUsesAttestedAllowance()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("foliage-valid", json =>
        {
            var profile = LimitsProfile();
            profile["queries"] = JsonNode.Parse("""
                {"maxCandidatePatches":65536,"maxInspectedFaces":1048576,"maxSupportIntersections":65536,
                 "maxPageReads":65536,"maxRecordReads":65536,"maxRecordDepth":128}
                """);
            profile["foliageContext"] = Family("foliageContext");
            profile["scene"]!["maxPreparedFaces"] = 1048576;
            profile["scene"]!["maxPreparedBytes"] = 536870912;
            profile["residency"]!["maxPlanBytes"] = 536870912;
            profile["residency"]!["maxQueuedBytes"] = 536870912;
            profile["residency"]!["maxResidentCpuBytes"] = 1073741824;
            json["limits"] = profile;
            int i = 0;
            foreach (var window in json["windows"]!.AsArray())
            {
                double edge = 1100 + i++;
                window!["minX"] = -edge; window["minZ"] = -edge;
                window["maxX"] = edge; window["maxZ"] = edge;
            }
        });
        try
        {
            PrivateOracleEntry.Run(fixture.Environment, (inputs, report) =>
                ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report));
        }
        catch (PrivateOracleFailure)
        {
            // These are invented public inputs. Real oracle reports never use this diagnostic route.
            Assert.Fail(string.Join("\n", File.ReadLines(fixture.ReportPath).Take(3))
                .Replace(fixture.SourceRoot, "<fixture>", StringComparison.Ordinal));
        }
        Assert.Empty(fixture.FailureCategories());
    }
}
