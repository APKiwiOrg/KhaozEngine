using System;
using System.IO;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData(256, true)]
    [InlineData(16, false)]
    public void SceneLimits_NestedNativeQueriesUseAttestedProfile(int pages, bool accepts)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        {
            var profile = LimitsProfile();
            profile["queries"] = JsonNode.Parse("""
                {"maxCandidatePatches":128,"maxInspectedFaces":8192,"maxSupportIntersections":128,
                 "maxPageReads":256,"maxRecordReads":128,"maxRecordDepth":128}
                """);
            profile["queries"]!["maxPageReads"] = pages;
            json["limits"] = profile;
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var document = inputs.Reload(true); var closure = inputs.Assets(document);
        MapSceneQueryFixtures.AddNativeChain(document);
        string directory = Path.Combine(Path.GetTempPath(), "khaozengine-oracle-query-" + Guid.NewGuid().ToString("N"));
        try
        {
            MapDocumentFile.SaveTiled(document, directory);
            using var disk = MapDocumentSource.OpenTiled(directory);
            var source = ShippedSceneComparison.CaptureSource(disk, closure, inputs.Manifest).Source!;
            var query = inputs.Manifest.Limits!.Queries;
            var scope = ShippedSceneInputs.Scope(new("query", new(.1, -1, -.9, .9, 1, -.1), null, 0), query);
            var result = source.Acquire(scope);
            if (accepts)
            {
                Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
                Assert.Equal(40, result.Scene!.SurfaceView.Witness.Present.Count);
                Assert.Equal(query, result.Scene.SurfaceView.Scope.Limits);
            }
            else
            {
                Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
                Assert.Equal(new MapSceneCapacity("MaxPageReads", pages + 1, pages), result.Capacity);
                Assert.Contains("MaxPageReads", Assert.Single(result.Findings));
                Assert.Contains("allowance 16", result.Findings[0]);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void SceneLimits_QueryProfilesAreClosedPositiveIntegers()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var provenance = JsonNode.Parse(File.ReadAllText(fixture.Environment["KHAOZ_R2_ORACLE_PROVENANCE"]!))!;
        var manifest = provenance["comparison"]!.AsObject();
        string[] fields = ["maxCandidatePatches", "maxInspectedFaces", "maxSupportIntersections", "maxPageReads", "maxRecordReads", "maxRecordDepth"];
        foreach (string field in fields)
            foreach (string value in new[] { "0", "-1", "null", "1.5", "2147483648", "1e100", "missing" })
            {
                var profile = LimitsProfile();
                var queries = new JsonObject();
                foreach (string name in fields) queries[name] = 256;
                if (value == "missing") queries.Remove(field);
                else queries[field] = JsonNode.Parse(value);
                profile["queries"] = queries; manifest["limits"] = profile;
                Assert.Throws<InvalidDataException>(() => ShippedSceneManifest.Read(System.Text.Json.JsonSerializer.SerializeToElement(manifest)));
            }
        var unknown = new JsonObject();
        foreach (string field in fields) unknown[field] = 256;
        unknown["unknown"] = 1;
        var extra = LimitsProfile(); extra["queries"] = unknown; manifest["limits"] = extra;
        Assert.Throws<InvalidDataException>(() => ShippedSceneManifest.Read(System.Text.Json.JsonSerializer.SerializeToElement(manifest)));
        foreach (string shape in new[] { "null", "[]", "{}", "{\"unknown\":1}" })
        {
            var profile = LimitsProfile(); profile["queries"] = JsonNode.Parse(shape); manifest["limits"] = profile;
            Assert.Throws<InvalidDataException>(() => ShippedSceneManifest.Read(System.Text.Json.JsonSerializer.SerializeToElement(manifest)));
        }
    }

}
