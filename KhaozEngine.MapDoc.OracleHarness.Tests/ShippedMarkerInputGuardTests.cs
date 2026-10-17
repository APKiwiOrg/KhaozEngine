using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.Tests.MapDoc;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class ShippedMarkerInputGuardTests
{
    [Fact]
    public void BoundImportPolicy_RefusesExplicitNativeHeightDespiteManifestClaim()
    {
        using var fixture = ShippedMarkerGuardFixtures.Create("claimed-bound");
        var inputs = ShippedMarkerInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Throws<InvalidDataException>(inputs.RequireBoundImports);
    }

    [Fact]
    public void AcceptedFormatSevenBuilding_LoadsCurrentWithoutRewritingScaffold()
    {
        using var fixture = ShippedBuildingGuardFixtures.Create("valid");
        var environment = fixture.Environment.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        string native = Path.Combine(environment["KHAOZ_R2_SHIPPED_SOURCE"]!, "native.json");
        var scaffold = JsonNode.Parse(File.ReadAllText(native))!;
        scaffold["formatVersion"] = 7;
        File.WriteAllText(native, scaffold.ToJsonString());
        string digest = AssertFixtures.Sha256(native);
        string provenancePath = environment["KHAOZ_R2_ORACLE_PROVENANCE"]!;
        var provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!;
        provenance["paths"]!.AsArray().Single(p => p!["path"]!.GetValue<string>() == "native.json")!["sha256"] = digest;
        var typed = JsonSerializer.Deserialize<ShippedSourceProvenance>(provenance.ToJsonString(), ShippedSourceProvenance.JsonOptions)!;
        provenance["aggregateSha256"] = typed.ComputeAggregate();
        File.WriteAllText(provenancePath, provenance.ToJsonString());
        environment["KHAOZ_R2_ORACLE_PROVENANCE_SHA256"] = AssertFixtures.Sha256(provenancePath);
        var inputs = ShippedBuildingInputs.Read(PrivateOracleInputs.Require(environment));
        Assert.Equal(MapDocumentFile.CurrentFormatVersion, inputs.NativeDocument.FormatVersion);
        Assert.Equal(digest, AssertFixtures.Sha256(native));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("yaw-full-turn")]
    [InlineData("all-bound")]
    [InlineData("support-only")]
    [InlineData("space-binding")]
    public void InventedExtraction_ComparesEveryMarkerOnIndependentStorageHeads(string variant)
    {
        using var fixture = ShippedMarkerGuardFixtures.Create(variant);
        var inputs = ShippedMarkerInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Equal(6, inputs.World.AllMarkers().Count());
        var compared = ShippedMarkerComparison.Compare(inputs);
        Assert.True(compared.Failures.Count == 0, compared.DetailJson);
        using var detail = JsonDocument.Parse(compared.DetailJson);
        Assert.Equal(4, detail.RootElement.GetProperty("heads").GetInt32());
        Assert.Equal(6, detail.RootElement.GetProperty("sourceMarkers").GetInt32());
        Assert.Contains("deferred R11 acceptance", compared.DetailJson);
        if (variant == "all-bound") inputs.RequireBoundImports();
        else Assert.Throws<InvalidDataException>(inputs.RequireBoundImports);
    }

    [Theory]
    [InlineData("tags", "marker-tags")]
    [InlineData("height", "marker-height")]
    [InlineData("negative-x", "marker-position")]
    [InlineData("yaw-quarter-turn", "marker-yaw")]
    [InlineData("role", "marker-role")]
    [InlineData("space", "marker-space")]
    [InlineData("parents", "marker-parents")]
    [InlineData("name", "marker-name")]
    [InlineData("id", "marker-inventory")]
    [InlineData("enabled", "marker-enabled")]
    [InlineData("archetype", "marker-archetype")]
    [InlineData("extra-native", "marker-inventory")]
    [InlineData("binding", "marker-binding")]
    [InlineData("expectation", "expectation-total")]
    [InlineData("standing-rule", "marker-height")]
    [InlineData("wrong-source-count", "source-inventory")]
    [InlineData("wrong-support-owner", "source-support")]
    public void SemanticMismatch_UsesFixedCategoriesAndSecureDetails(string variant, string category)
    {
        using var fixture = ShippedMarkerGuardFixtures.Create(variant);
        ShippedMarkerReport? compared = null;
        var failure = Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (i, report) =>
        {
            compared = ShippedMarkerComparison.Compare(ShippedMarkerInputs.Read(i));
            report.Record("markers", compared.DetailJson);
            report.Check("marker-parity", compared.Failures.Count == 0, compared.DetailJson);
        }));
        Assert.NotNull(compared);
        Assert.Contains(category, compared.Failures);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.PublicSecretMarker, failure.ToString());
        Assert.Matches("^private oracle: [0-9]+ marker-parity mismatches, report sha256 [0-9a-f]{64}$", failure.Message);
        Assert.Contains("failures", File.ReadAllText(fixture.ReportPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(fixture.ReportPath));
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("missing-provenance")]
    [InlineData("missing-digest")]
    [InlineData("missing-report")]
    [InlineData("wrong-digest")]
    [InlineData("unattested-catalog")]
    [InlineData("unattested-region")]
    [InlineData("unattested-generator")]
    [InlineData("unattested-helper")]
    [InlineData("unattested-resource")]
    [InlineData("unattested-prefab")]
    [InlineData("missing-support")]
    [InlineData("duplicate-comparison")]
    [InlineData("case-comparison")]
    [InlineData("duplicate-role-field")]
    [InlineData("case-role-field")]
    [InlineData("unknown-role-field")]
    [InlineData("duplicate-source")]
    [InlineData("duplicate-destination")]
    [InlineData("duplicate-index")]
    [InlineData("wrong-index-tags")]
    [InlineData("wrong-region-owner")]
    [InlineData("missing-role")]
    [InlineData("extra-role")]
    [InlineData("missing-marker-row")]
    [InlineData("extra-marker-row")]
    [InlineData("unknown-height-rule")]
    [InlineData("unsupported-schema")]
    [InlineData("unsupported-native")]
    [InlineData("escape-path")]
    [InlineData("parent-link")]
    [InlineData("final-link")]
    [InlineData("dangling-link")]
    [InlineData("unsafe-report")]
    [InlineData("existing-report")]
    [InlineData("linked-report")]
    public void IncompleteOrUnsafeClosure_RefusesWithoutDisclosingSyntheticSentinel(string variant)
    {
        using var fixture = ShippedMarkerGuardFixtures.Create(variant);
        var failure = Assert.ThrowsAny<Exception>(() => PrivateOracleEntry.Run(fixture.Environment, (i, report) =>
        {
            var inputs = ShippedMarkerInputs.Read(i);
            var compared = ShippedMarkerComparison.Compare(inputs);
            report.Check("marker-parity", compared.Failures.Count == 0, compared.DetailJson);
        }));
        Assert.True(failure is PrivateOracleInputException or PrivateOracleFailure, failure.GetType().Name);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.PublicSecretMarker, failure.ToString());
    }

    [Theory]
    [InlineData("world/regions/r_0_0.json")]
    [InlineData("catalog.json")]
    [InlineData("native.json")]
    [InlineData("assets/public.mesh")]
    public void ChangedAfterOuterAttestation_IsRefusedBeforeComparison(string path)
    {
        using var fixture = ShippedMarkerGuardFixtures.Create("valid");
        var entry = PrivateOracleInputs.Require(fixture.Environment);
        File.AppendAllText(Path.Combine(fixture.SourceRoot, path), fixture.PublicSecretMarker);
        Assert.Throws<InvalidDataException>(() => ShippedMarkerInputs.Read(entry));
    }

    [Fact]
    public void CapturedNativeAndSourceBytes_SurviveLaterFileChanges()
    {
        using var fixture = ShippedMarkerGuardFixtures.Create("valid");
        var inputs = ShippedMarkerInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        foreach (string path in new[] { "world/regions/r_0_0.json", "catalog.json", "native.json", "assets/public.mesh" })
            File.WriteAllText(Path.Combine(fixture.SourceRoot, path), fixture.PublicSecretMarker);
        var result = ShippedMarkerComparison.Compare(inputs);
        Assert.True(result.Failures.Count == 0, result.DetailJson);
    }
}
