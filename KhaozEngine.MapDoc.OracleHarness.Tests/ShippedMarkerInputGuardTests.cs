using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Netcode;
using KhaozEngine.TileWorld.Physics;
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

    [Theory]
    [InlineData("valid")]
    [InlineData("yaw-full-turn")]
    [InlineData("all-bound")]
    [InlineData("support-only")]
    [InlineData("space-binding")]
    [InlineData("building-manifest")]
    [InlineData("format-eight-native")]
    public void InventedExtraction_ComparesEveryMarkerOnIndependentStorageHeads(string variant)
    {
        using var fixture = ShippedMarkerGuardFixtures.Create(variant);
        var inputs = ShippedMarkerInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.Equal(7, inputs.World.AllMarkers().Count());
        var compared = ShippedMarkerComparison.Compare(inputs);
        Assert.True(compared.Failures.Count == 0, compared.DetailJson);
        using var detail = JsonDocument.Parse(compared.DetailJson);
        Assert.Equal(4, detail.RootElement.GetProperty("heads").GetInt32());
        Assert.Equal(7, detail.RootElement.GetProperty("sourceMarkers").GetInt32());
        Assert.Contains("deferred R11 acceptance", compared.DetailJson);
        if (variant == "all-bound") inputs.RequireBoundImports();
        else Assert.Throws<InvalidDataException>(inputs.RequireBoundImports);
    }

    [Fact]
    public void PrefabMarker_ExpandsJustWestOfItsSourcePoint()
    {
        using var fixture = ShippedMarkerGuardFixtures.Create("valid");
        var inputs = ShippedMarkerInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var resolved = MapNativeResolution.Resolve(inputs.ReloadDocument(), inputs.ReloadAssets(), new("marker-guard", 1, "marker-guard/1", 2));
        var marker = MapEffectiveAnnotations.Markers(resolved.EffectiveResources!).Single(m => m.Id == ShippedMarkerGuardFixtures.PrefabMarkerId);
        Assert.Equal("kit", marker.InstanceId);
        // The source point is tile 1 plus a half tile. The exact native point sits below it, inside the source tolerance.
        double x = marker.Position.X.ToDouble();
        Assert.True(x < 1.5 && 1.5 - x <= ShippedMarkerComparison.PositionTolerance, x.ToString("R", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void TwistedTile_ServerGroundDiffersFromPresenterBilinear()
    {
        using var fixture = ShippedMarkerGuardFixtures.Create("valid");
        var inputs = ShippedMarkerInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        float tileX = ShippedMarkerGuardFixtures.TwistedTileX, tileZ = ShippedMarkerGuardFixtures.TwistedTileZ;
        float server = TileWorldColliders.Build(inputs.World, inputs.Catalogs).Ground
            .HeightAt(TileWorldSpace.WorldX(tileX, inputs.World.TileSize), TileWorldSpace.WorldZ(tileZ, inputs.World.TileSize));
        float bilinear = new TileDocumentGroundHeight(inputs.World).HeightAt(tileX, tileZ, 0);
        Assert.True(Math.Abs(server - bilinear) > 0.1, server.ToString("R", CultureInfo.InvariantCulture) + " " + bilinear.ToString("R", CultureInfo.InvariantCulture));
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
    [InlineData("twisted-bilinear", "marker-height")]
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
            report.CheckFailures("marker-parity", compared.ClassifiedFailures);
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
            report.CheckFailures("marker-parity", compared.ClassifiedFailures);
        }));
        Assert.True(failure is PrivateOracleInputException or PrivateOracleFailure, failure.GetType().Name);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.PublicSecretMarker, failure.ToString());
    }

    // Each variant passes the entry boundary and only the marker reader refuses it, so the message names the rule.
    // The reader's own link check in its snapshot is not here. Verify already refuses every reparse point in the
    // extraction before the snapshot walks a path, so only a race could reach it.
    [Theory]
    [InlineData("duplicate-comparison", "duplicate or case-variant JSON member")]
    [InlineData("duplicate-role-field", "duplicate or case-variant JSON member")]
    [InlineData("case-duplicate-role-field", "duplicate or case-variant JSON member")]
    [InlineData("unattested-generator", "unattested marker dependency")]
    [InlineData("tile-size", "marker source terrain conversion requires metre tiles")]
    [InlineData("upper-plane", "server marker height covers plane 0 only")]
    [InlineData("absent-owner", "unknown source support owner")]
    [InlineData("nonpositive-owner", "unknown source support owner")]
    public void ReaderOnlyRefusal_NamesItsSpecificReason(string variant, string reason)
    {
        using var fixture = ShippedMarkerGuardFixtures.Create(variant);
        var entry = PrivateOracleInputs.Require(fixture.Environment);
        var error = Assert.Throws<InvalidDataException>(() => ShippedMarkerInputs.Read(entry));
        Assert.Equal(reason, error.Message);
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
