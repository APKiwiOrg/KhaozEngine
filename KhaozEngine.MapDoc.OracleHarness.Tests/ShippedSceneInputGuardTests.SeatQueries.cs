using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData(false, 128, true)]
    [InlineData(true, 128, true)]
    [InlineData(false, 16, false)]
    [InlineData(true, 16, false)]
    public void SceneLimits_PlacementAndMarkerSeatsUseAttestedRecordDepth(bool marker, int depth, bool accepts)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        {
            var profile = LimitsProfile();
            profile["queries"] = JsonNode.Parse("""
                {"maxCandidatePatches":128,"maxInspectedFaces":8192,"maxSupportIntersections":128,
                 "maxPageReads":256,"maxRecordReads":128,"maxRecordDepth":128}
                """);
            profile["queries"]!["maxRecordDepth"] = depth;
            json["limits"] = profile;
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var document = inputs.Reload(true);
        var closure = inputs.Assets(document);
        MapSceneQueryFixtures.AddNativeSeat(document, marker, "native-column");
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-oracle-seat-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(document, directory);
            using var disk = MapDocumentSource.OpenTiled(directory);
            var source = ShippedSceneComparison.CaptureSource(disk, closure, inputs.Manifest).Source!;
            var query = inputs.Manifest.Limits!.Queries;
            var scope = ShippedSceneInputs.Scope(new("seat", new(.1, -1, -.9, .9, 1, -.1), null, 0), query);
            var result = source.Acquire(scope);
            if (accepts)
            {
                Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
                Assert.Equal(query, result.Scene!.SurfaceView.Scope.Limits);
            }
            else
            {
                Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
                Assert.Equal(new MapSceneCapacity("MaxRecordDepth", depth + 1, depth), result.Capacity);
                Assert.Contains("allowance " + depth, Assert.Single(result.Findings));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
