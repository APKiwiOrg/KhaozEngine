using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void SceneLimits_UploadExhaustionPreservesDeclaredGpuAllowance()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        { var profile = LimitsProfile(); profile["residency"]!["maxResidentGpuBytes"] = 1; json["limits"] = profile; });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.Compare(inputs, report)));
        string detail = File.ReadAllText(fixture.ReportPath);
        Assert.Contains("scene replacement refused CapacityExceeded", detail);
        Assert.Contains("MaxResidentGpuBytes", detail);
        Assert.Contains("requested ", detail);
        Assert.Contains("allowance 1", detail);
    }

    [Fact]
    public void SceneLimits_ResidencyRefusalPreservesBudgetDetail()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var document = inputs.Reload(true); var closure = inputs.Assets(document);
        using var docs = MapDocumentSource.FromDocument(document);
        var source = MapSceneSource.Capture(docs, closure).Source!;
        var proof = MapRenderExtentValidator.Validate(closure, source.RequiredAssetIds, new(new())).Proofs!;
        source = source.WithRenderExtents(proof).Source!;
        var window = ShippedSceneInputs.Scope(inputs.Manifest.Windows[0]);
        var scheduler = new SceneOracleScheduler();
        using var residency = new MapRenderResidency(new(source, closure, proof, inputs.NativePolicy.Native(closure, inputs.Manifest), [window, window]),
            new SceneOracleDevice(), new(MaxActiveWindows: 1), scheduler);
        var error = Assert.Throws<InvalidDataException>(() => ShippedSceneComparison.Complete(residency, scheduler));
        Assert.Equal(new MapSceneCapacity("MaxActiveWindows", 2, 1), residency.InspectReadiness().Transition.Capacity);
        Assert.Contains("MaxActiveWindows", error.Message);
        Assert.Contains("requested 2", error.Message);
        Assert.Contains("allowance 1", error.Message);
    }
    [Fact]
    public void SceneLimits_AttestedProfileAcceptsExplicitAllowances()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json => json["limits"] = LimitsProfile());
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report));
        Assert.Empty(fixture.FailureCategories());
    }

    [Fact]
    public void SceneLimits_DeclaredResidencyBudgetIsUsedByStreaming()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        { var profile = LimitsProfile(); profile["residency"]!["maxActiveWindows"] = 1; json["limits"] = profile; });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.Compare(inputs, report)));
        string detail = File.ReadAllText(fixture.ReportPath);
        Assert.Contains("MaxActiveWindows", detail);
        Assert.Contains("requested 2", detail);
        Assert.Contains("allowance 1", detail);
    }

    [Fact]
    public void SceneLimits_ProfileChangesRequireFreshProvenanceAttestation()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json => json["limits"] = LimitsProfile());
        string path = fixture.Environment["KHAOZ_R2_ORACLE_PROVENANCE"]!;
        var provenance = JsonNode.Parse(File.ReadAllText(path))!;
        provenance["comparison"]!["limits"]!["scene"]!["maxSelectedResources"] = 5000;
        File.WriteAllText(path, provenance.ToJsonString(ShippedScenePolicy.Json));
        Assert.Throws<PrivateOracleInputException>(() => ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment)));
    }

    [Theory]
    [InlineData(null, 4096, false)]
    [InlineData(5000, 5000, true)]
    [InlineData(4095, 4095, false)]
    public void SceneLimits_SelectedResourceAllowanceControlsSyntheticWindow(int? selected, int allowance, bool accepts)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid", json =>
        {
            if (selected is { } value) { var profile = LimitsProfile(); profile["scene"]!["maxSelectedResources"] = value; json["limits"] = profile; }
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var document = inputs.Reload(true); var closure = inputs.Assets(document);
        var template = document.Placements.Single();
        document.Placements.Clear();
        for (int i = 1; i <= 4097; i++)
            document.Placements.Add(new() { Id = "invented-" + i, NumericId = i, Kind = template.Kind, AssetId = template.AssetId, X = template.X, Y = template.Y, Z = template.Z });
        document.NumericIdHighWaterMark = 4097;
        using var docs = MapDocumentSource.FromDocument(document);
        // The profile is consumed by the same source factory as the full oracle entry.
        var source = ShippedSceneComparison.CaptureSource(docs, closure, inputs.Manifest).Source!;
        var result = source.Acquire(ShippedSceneInputs.Scope(inputs.Manifest.Windows[0]));
        if (accepts) Assert.NotNull(result.Scene);
        else
        {
            Assert.Equal(MapSceneStatus.CapacityExceeded, result.Status);
            Assert.Equal(new MapSceneCapacity("MaxSelectedResources", allowance + 1, allowance), result.Capacity);
            Assert.Contains("allowance " + allowance, Assert.Single(result.Findings));
        }
    }

    [Fact]
    public void SceneLimits_RejectsInvalidOrIncompleteProfiles()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var provenance = JsonNode.Parse(File.ReadAllText(fixture.Environment["KHAOZ_R2_ORACLE_PROVENANCE"]!))!;
        var manifest = provenance["comparison"]!.AsObject();
        foreach (string invalid in new[] { "null", "{}", "{\"scene\":{}}", "{\"scene\":{},\"residency\":{},\"unknown\":1}" })
        {
            manifest["limits"] = JsonNode.Parse(invalid);
            Assert.Throws<InvalidDataException>(() => ShippedSceneManifest.Read(JsonSerializer.SerializeToElement(manifest)));
        }
        foreach (var invalid in InvalidSceneLimits())
        {
            var profile = LimitsProfile();
            string section = (string)invalid[0], field = (string)invalid[1], value = (string)invalid[2];
            if (value == "missing") profile[section]!.AsObject().Remove(field);
            else profile[section]![field] = JsonNode.Parse(value);
            manifest["limits"] = profile;
            Assert.Throws<InvalidDataException>(() => ShippedSceneManifest.Read(JsonSerializer.SerializeToElement(manifest)));
        }
    }

    public static IEnumerable<object[]> InvalidSceneLimits()
    {
        foreach (var section in LimitsProfile())
            foreach (var field in section.Value!.AsObject())
                foreach (string invalid in new[] { "0", "-1", "1.5", "1e100", "\"NaN\"", "null", "missing" })
                    yield return new object[] { section.Key, field.Key, invalid };
        yield return new object[] { "scene", "unknown", "1" };
        yield return new object[] { "residency", "maxActiveWindows", "2147483648" };
        yield return new object[] { "scene", "maxPreparedBytes", "9223372036854775808" };
    }

    static JsonObject LimitsProfile() => JsonNode.Parse("""
        {"scene":{"maxTiles":65536,"maxMetadataRows":65536,"maxMemberships":262144,"maxMembershipsPerResource":4096,
        "maxScannedBytes":134217728,"maxMetadataBytes":67108864,"maxSelectedResources":4096,"maxPatchScopes":256,
        "maxPreparedFaces":262144,"maxPreparedBytes":67108864},
        "residency":{"maxWorkers":2,"maxQueuedPlans":32,"maxQueuedBytes":67108864,"maxAppliesPerUpdate":2,
        "maxUploadBytesPerUpdate":4194304,"maxDisposalsPerUpdate":8,"maxResidentCpuBytes":268435456,
        "maxResidentGpuBytes":536870912,"maxPlanBytes":67108864,"maxActiveWindows":256,
        "maxInFlightUploadCpuBytes":268435456,"maxInFlightUploadStagingGpuBytes":134217728}}
        """)!.AsObject();

}
