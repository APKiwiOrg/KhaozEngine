using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Theory]
    [InlineData("decode", "maxVertices")]
    [InlineData("assets", "maxDecodedBytes")]
    [InlineData("foliage", "maxCandidates")]
    [InlineData("foliageContext", "maxPlacements")]
    [InlineData("water", "maxTriangles")]
    [InlineData("refinement", "maxContextPatches")]
    public void SceneLimits_AdditionalFamiliesAreAcceptedAndAttested(string family, string field)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        {
            var profile = LimitsProfile();
            profile[family] = Family(family);
            json["limits"] = profile;
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.NotNull(inputs.Manifest.Limits);
        string path = fixture.Environment["KHAOZ_R2_ORACLE_PROVENANCE"]!;
        var provenance = JsonNode.Parse(File.ReadAllText(path))!;
        provenance["comparison"]!["limits"]![family]![field] = 1;
        File.WriteAllText(path, provenance.ToJsonString(ShippedScenePolicy.Json));
        Assert.Throws<PrivateOracleInputException>(() => ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment)));
    }

    [Theory]
    [InlineData("decode", "maxVertices", "MaxVertices", "valid")]
    [InlineData("decode", "maxPeakBytes", "MaxPeakBytes", "valid")]
    [InlineData("assets", "maxDecodedBytes", "MaxDecodedBytes", "valid")]
    [InlineData("foliage", "maxCandidates", "MaxCandidates", "foliage-valid")]
    [InlineData("foliageContext", "maxSourceUtf8Bytes", "MaxSourceUtf8Bytes", "foliage-valid")]
    [InlineData("residency", "maxInFlightUploadCpuBytes", "MaxInFlightUploadCpuBytes", "valid")]
    [InlineData("residency", "maxInFlightUploadStagingGpuBytes", "MaxInFlightUploadStagingGpuBytes", "valid")]
    [InlineData("water", "maxTriangles", "MaxTriangles", "water-valid")]
    public void SceneLimits_AdditionalBudgetsReachEngineCalls(string family, string field, string limit, string variant)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant, json =>
        {
            var profile = LimitsProfile();
            var group = profile[family]?.DeepClone().AsObject() ?? Family(family); group[field] = 1; profile[family] = group;
            json["limits"] = profile;
        });
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (inputs, report) => ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report)));
        string detail = File.ReadAllText(fixture.ReportPath);
        Assert.True(detail.Contains(limit, StringComparison.Ordinal), detail);
        Assert.Contains("allowance 1", detail);
    }

    [Fact]
    public void SceneLimits_RegisteredScalarFieldsHaveManifestCoverage()
    {
        Assert.Equal(67, System.Linq.Enumerable.Sum(SceneLimitsProfile.Families.Values, t => SceneLimitsProfile.Fields(t).Count));
    }

    [Fact]
    public void SceneLimits_ProofTableBudgetReachesExtentValidation()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        { var profile = LimitsProfile(); profile["maxProofBytes"] = 1; json["limits"] = profile; });
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (inputs, report) => ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report)));
        string detail = File.ReadAllText(fixture.ReportPath);
        Assert.True(detail.Contains("MaxProofBytes", StringComparison.Ordinal), detail);
        Assert.Contains("requested 256", detail);
        Assert.Contains("allowance 1", detail);
    }

    [Fact]
    public void SceneLimits_AdditionalFamiliesAreClosedPositiveIntegers()
    {
        foreach (string family in new[] { "decode", "assets", "foliage", "foliageContext", "water", "refinement" })
        {
            foreach (var parameter in SceneLimitsProfile.Fields(SceneLimitsProfile.Families[family]))
            {
                string field = SceneLimitsProfile.Field(parameter);
                string overflow = parameter.ParameterType == typeof(int) ? "2147483648" : "9223372036854775808";
                foreach (string value in new[] { "0", "-1", "null", "1.5", "1e100", overflow, "missing" })
                {
                    var profile = LimitsProfile(); var group = Family(family);
                    if (value == "missing") group.Remove(field); else group[field] = JsonNode.Parse(value);
                    profile[family] = group;
                    Assert.Throws<InvalidDataException>(() => SceneLimitsProfile.Read(JsonSerializer.SerializeToElement(profile)));
                }
            }
            foreach (string shape in new[] { "null", "[]", "{}", "{\"unknown\":1}" })
            {
                var profile = LimitsProfile(); profile[family] = JsonNode.Parse(shape);
                Assert.Throws<InvalidDataException>(() => SceneLimitsProfile.Read(JsonSerializer.SerializeToElement(profile)));
            }
            var extra = LimitsProfile(); var fields = Family(family); fields["unapproved"] = 1; extra[family] = fields;
            Assert.Throws<InvalidDataException>(() => SceneLimitsProfile.Read(JsonSerializer.SerializeToElement(extra)));
            var complete = LimitsProfile(); complete[family] = Family(family);
            string text = complete.ToJsonString();
            string first = System.Linq.Enumerable.First(Family(family)).Key;
            using var duplicate = JsonDocument.Parse(text.Replace("\"" + first + "\":", "\"" + first + "\":1,\"" + first + "\":", StringComparison.Ordinal));
            Assert.Throws<InvalidDataException>(() => SceneLimitsProfile.Read(duplicate.RootElement));
            fields = Family(family); var original = fields[first]!.DeepClone(); fields.Remove(first); fields["X" + first] = original;
            complete[family] = fields;
            Assert.Throws<InvalidDataException>(() => SceneLimitsProfile.Read(JsonSerializer.SerializeToElement(complete)));
        }
        foreach (string value in new[] { "0", "-1", "null", "1.5", "1e100", "9223372036854775808" })
        {
            var profile = LimitsProfile(); profile["maxProofBytes"] = JsonNode.Parse(value);
            Assert.Throws<InvalidDataException>(() => SceneLimitsProfile.Read(JsonSerializer.SerializeToElement(profile)));
        }
    }

    [Fact]
    public void SceneLimits_OmittedFamiliesKeepReleasedDefaults()
    {
        var profile = SceneLimitsProfile.Read(JsonSerializer.SerializeToElement(LimitsProfile()));
        Assert.Null(profile.Decode); Assert.Null(profile.Assets); Assert.Null(profile.Scene.Preparation);
        Assert.Null(profile.Queries.Refinement); Assert.Null(profile.MaxProofBytes); Assert.False(profile.HasQueryProfile);
    }

    [Fact]
    public void SceneLimits_CompleteRegisteredProfilePassesWholeScenePath()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        {
            var profile = LimitsProfile();
            foreach (string family in new[] { "decode", "assets", "foliage", "foliageContext", "water", "refinement" }) profile[family] = Family(family);
            profile["maxProofBytes"] = 16777216;
            json["limits"] = profile;
        });
        PrivateOracleEntry.Run(fixture.Environment, (inputs, report) => ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report));
        Assert.Empty(fixture.FailureCategories());
    }

    static JsonObject Family(string family) => JsonNode.Parse(family switch
    {
        "decode" => """{"maxPeakBytes":33554432,"maxVertices":1048576,"maxIndices":3145728,"maxImagePixels":4194304,"maxImages":256}""",
        "assets" => """{"maxDecodedBytes":268435456,"maxParts":4096,"maxLods":16}""",
        "foliage" => """{"maxCandidates":4000000,"maxSamples":262144,"maxPrepareScopes":256,"maxFaces":262144,"maxRuleCandidates":4096,"maxMaskCandidates":4096,"maxQueryChunks":64,"maxQuerySamples":262144,"maxQueryRetainedBytes":67108864,"maxDifferentialCandidates":64000000,"maxChangeRows":4000000,"maxComparisonPreparedChunks":64,"maxComparisonRetainedBytes":134217728}""",
        "foliageContext" => """{"maxSourceUtf8Bytes":134217728,"maxChunkDescriptors":65536,"maxPlacements":65536,"maxSurfacePatches":65536,"maxPaintTargets":4096}""",
        "water" => """{"maxPieces":4096,"maxPieceVertices":64,"maxFootprintCells":4194304,"maxBoundFaces":1048576,"maxPairChecks":65536,"maxTriangles":1048576,"maxBoundCells":4194304}""",
        "refinement" => """{"maxSourceFacesPerBound":16384,"maxPairChecks":65536,"maxRefinementFaces":32768,"maxRefinementVertices":131072,"maxContextPatches":256,"maxContextFaces":262144}""",
        _ => throw new ArgumentException(nameof(family)),
    })!.AsObject();
}
