using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using Xunit;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageGuardFixtures;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Public synthetic guards for the private foliage contract. Every fixture is invented, and discovery reads
/// no environment variable or file.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ShippedFoliageInputGuardTests
{
    [Fact]
    public void InventedExtraction_ComparesEveryCandidateOnIndependentStorageHeads()
    {
        using var fixture = Create("valid");
        PrivateOracleEntry.Run(fixture.Environment, (inputs, report) => ShippedFoliageComparison.Compare(ShippedFoliageInputs.Read(inputs), report));

        JsonElement summary = Summary(fixture);
        int validSyntheticFailures = summary.GetProperty("failures").EnumerateObject().Sum(p => p.Value.GetInt32());
        Assert.Equal(0, validSyntheticFailures);
        Assert.Equal(ShippedFoliageComparison.Scope, summary.GetProperty("scope").GetString());
        Assert.Equal(4, summary.GetProperty("heads").GetInt32());
        JsonElement counts = summary.GetProperty("counts");
        Assert.Equal(4, counts.GetProperty("headMatches").GetInt64());
        Assert.Equal(2, counts.GetProperty("layers").GetInt64());
        Assert.Equal(9, counts.GetProperty("sourceOwners").GetInt64());
        Assert.True(counts.GetProperty("accepted").GetInt64() > 0);
        Assert.Equal(4 * counts.GetProperty("ownedCandidates").GetInt64(), counts.GetProperty("comparedRows").GetInt64());
        // Every released stage the invented world exercises is reached by the legacy sampler and matched natively.
        var stages = summary.GetProperty("breakdowns").GetProperty("rejections").EnumerateObject().Select(p => p.Name).ToHashSet();
        // EdgeFade, VoidUnderlay and AbsentRegion compare end to end as EdgeZero, MissingUnderlay and OutsideDomain.
        foreach (string stage in new[] { "ZeroRaster", "AbsentRegion", "VoidUnderlay", "UnderlayNotAllowed", "Water", "Indoors", "Solid",
            "Roof", "Door", "EdgeFade", "DensityRejected" })
            Assert.Contains(stage, stages);
        // Headless legacy sampling started no window or GPU backend in this host.
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name is { } name &&
            (name.StartsWith("Silk.NET.Windowing", StringComparison.Ordinal) || name.StartsWith("Silk.NET.GLFW", StringComparison.Ordinal) ||
             name.StartsWith("KhaozEngine.Gpu.", StringComparison.Ordinal) || name == "KhaozEngine.Windowing"));
    }

    [Fact]
    public void PrivateSeatedOrdinals_RejectWrongSurfaceMappingAndMixedOwnerApplicability()
    {
        using (var fixture = Create("valid"))
        {
            var effects = ShippedFoliageInputs.Read(PrivateOracleInputs.Require(fixture.Environment)).OwnerEffects;
            bool Has(long owner, string layer, MapFoliageClearanceKind kind) => effects.Contains(new(owner, layer, kind));
            // Same asset and tag: the higher roof slab clears the plane 0 layer, its level peer clears nothing.
            Assert.True(Has(HighSlab, Cover, MapFoliageClearanceKind.RoofAbove));
            Assert.DoesNotContain(effects, e => e.SourceObjectId == LevelSlab);
            // One Solid rule per asset: level trees clear the plane 0 layer, the higher tree only its own plane's layer.
            Assert.True(Has(LevelTree, Cover, MapFoliageClearanceKind.Solid) && Has(FarTree, Cover, MapFoliageClearanceKind.Solid));
            Assert.False(Has(HighTree, Cover, MapFoliageClearanceKind.Solid));
            Assert.True(Has(HighTree, Upper, MapFoliageClearanceKind.Solid));
            // The higher canopy is a roof and a solid with an explicit-height seat: roof below, solid on its own plane.
            Assert.True(Has(HighCanopy, Cover, MapFoliageClearanceKind.RoofAbove) && Has(HighCanopy, Upper, MapFoliageClearanceKind.Solid));
            Assert.False(Has(HighCanopy, Cover, MapFoliageClearanceKind.Solid));
            Assert.True(Has(LevelCanopy, Cover, MapFoliageClearanceKind.Solid));
            Assert.False(Has(LevelCanopy, Cover, MapFoliageClearanceKind.RoofAbove));
            Assert.True(Has(LevelDoor, Cover, MapFoliageClearanceKind.Door) && Has(HighDoor, Upper, MapFoliageClearanceKind.Door));
            Assert.False(Has(HighDoor, Cover, MapFoliageClearanceKind.Door));
        }
        foreach (var (variant, reason) in new[]
        {
            ("seat-lower", "seated support differs from the source plane"),
            ("manifest-seat-lower", "seated support differs from the source plane"),
            ("explicit-lower", "seated support differs from the source plane"),
            ("table-wrong-ordinal", "compatibility table differs from source planes"),
            ("table-missing-row", "compatibility table differs from source planes"),
            ("table-extra-row", "compatibility table differs from source planes"),
            ("manifest-missing-row", "compatibility table differs from source planes"),
            ("target-peer-roof", "foliage clearing targets differ from source"),
            ("target-missing", "foliage clearing targets differ from source"),
        })
        {
            using var fixture = Create(variant);
            var entry = PrivateOracleInputs.Require(fixture.Environment);
            var error = Assert.Throws<InvalidDataException>(() => ShippedFoliageInputs.Read(entry));
            Assert.True(reason == error.Message, variant + ": " + error.Message);
        }
    }

    [Theory]
    [InlineData("changed-byte", "foliage-byte")]
    [InlineData("changed-decision", "foliage-decision")]
    [InlineData("changed-decision", "foliage-missing-sample")]
    [InlineData("extra-sample", "foliage-extra-sample")]
    [InlineData("wrong-seed", "foliage-settings")]
    [InlineData("wrong-seed", "foliage-channel")]
    [InlineData("wrong-weight", "foliage-model")]
    [InlineData("missing-chunk", "foliage-chunk")]
    [InlineData("tiled-byte", "foliage-head")]
    [InlineData("expectation", "expectation-accepted")]
    public void SemanticMismatch_UsesFixedCategoriesAndSecureDetails(string variant, string category)
    {
        using var fixture = Create(variant);
        var failure = Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (inputs, report) =>
            ShippedFoliageComparison.Compare(ShippedFoliageInputs.Read(inputs), report)));
        string publicFailureMessage = failure.ToString();
        Assert.Contains(category, Categories(fixture));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.PublicSecretMarker, publicFailureMessage);
        Assert.Matches("^private oracle: [0-9]+ [a-z0-9-]+ mismatches, report sha256 [0-9a-f]{64}$", failure.Message);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Report(fixture)));
    }

    [Fact]
    public void OneByteAndOneDecision_SensitivityControlsFail()
    {
        using (var fixture = Create("changed-byte"))
        {
            Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (inputs, report) =>
                ShippedFoliageComparison.Compare(ShippedFoliageInputs.Read(inputs), report)));
            IReadOnlyList<string> changedByteCategories = Categories(fixture);
            Assert.Contains("foliage-byte", changedByteCategories);
        }
        using (var fixture = Create("changed-decision"))
        {
            Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (inputs, report) =>
                ShippedFoliageComparison.Compare(ShippedFoliageInputs.Read(inputs), report)));
            IReadOnlyList<string> changedDecisionCategories = Categories(fixture);
            Assert.Contains("foliage-decision", changedDecisionCategories);
            Assert.DoesNotContain("foliage-byte", changedDecisionCategories);
        }
    }

    [Fact]
    public void ComparedRow_FlagsOneRankBitOneReasonAndOneModel()
    {
        using var fixture = Create("valid");
        var inputs = ShippedFoliageInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        FoliageLayerBinding binding = inputs.Layers.Single(l => l.Row.NativeLayerId == Cover);
        var document = inputs.ReloadDocument();
        var context = MapFoliageGenerationContext.Capture(document, inputs.ReloadAssets(document)).Context!;
        RectArea area = ShippedFoliageComparison.Area(new RegionCoord(0, 0));
        var expected = ShippedFoliageComparison.Expected(new TileFoliageSurface(inputs.World, inputs.Catalogs, binding.Source),
            ShippedFoliageComparison.Settings(binding.Source), area, out _);
        MapFoliageDecision actual = ShippedFoliageComparison.Actual(context, Cover, area)!.First(d => d.Sample is not null);
        FoliageExpectedRow want = expected[(actual.Address.X, actual.Address.Z)];
        MapFoliageLayer layer = context.Foliage!.Layers.Single(l => l.Id == Cover);
        MapFoliageSample s = actual.Sample!;
        MapFoliageSample Tampered(string? asset = null, float? rank = null) => new(s.Key, s.OccupiedSpaceId, s.Face, asset ?? s.AssetId,
            s.MaterialResourceIds, s.VisibleMaterial, s.Position, s.Orientation, s.Scale, s.Transform, rank ?? s.ThinningRank);

        Assert.Empty(ShippedFoliageComparison.CompareRow(binding, layer, context.Assets, want, actual));
        var changedRankCategories = ShippedFoliageComparison.CompareRow(binding, layer, context.Assets, want,
            actual with { Sample = Tampered(rank: BitConverter.UInt32BitsToSingle(BitConverter.SingleToUInt32Bits(s.ThinningRank) ^ 1)) });
        Assert.Contains("foliage-rank", changedRankCategories);
        Assert.Single(changedRankCategories);
        Assert.Contains("foliage-decision", ShippedFoliageComparison.CompareRow(binding, layer, context.Assets, want,
            actual with { Reason = MapFoliageReason.DensityRejected }));
        string other = s.AssetId == "native-reed-a" ? "native-reed-b" : "native-reed-a";
        Assert.Contains("foliage-model", ShippedFoliageComparison.CompareRow(binding, layer, context.Assets, want, actual with { Sample = Tampered(other) }));
        Assert.Contains("foliage-missing-sample", ShippedFoliageComparison.CompareRow(binding, layer, context.Assets, want,
            actual with { Sample = null, Reason = MapFoliageReason.DensityRejected }));
        // The full rotation angle, never a matrix component: a quarter turn about the normal is far outside tolerance.
        Assert.True(ShippedFoliageComparison.Angle(s.Orientation, System.Numerics.Quaternion.Concatenate(s.Orientation,
            System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, MathF.PI / 2))) > 1);
    }

    [Fact]
    public void ComparedRow_ChecksLayerAndSupportOwnerOnEveryRow()
    {
        using var fixture = Create("valid");
        var inputs = ShippedFoliageInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        FoliageLayerBinding binding = inputs.Layers.Single(l => l.Row.NativeLayerId == Cover);
        var document = inputs.ReloadDocument();
        var context = MapFoliageGenerationContext.Capture(document, inputs.ReloadAssets(document)).Context!;
        MapFoliageLayer layer = context.Foliage!.Layers.Single(l => l.Id == Cover);
        RectArea area = ShippedFoliageComparison.Area(new RegionCoord(0, 0));
        var expected = ShippedFoliageComparison.Expected(new TileFoliageSurface(inputs.World, inputs.Catalogs, binding.Source),
            ShippedFoliageComparison.Settings(binding.Source), area, out _);
        MapFoliageDecision rejected = ShippedFoliageComparison.Actual(context, Cover, area)!.First(d => d.Reason == MapFoliageReason.Solid);
        FoliageExpectedRow want = expected[(rejected.Address.X, rejected.Address.Z)];
        IReadOnlyList<string> Compare(MapFoliageDecision row) => ShippedFoliageComparison.CompareRow(binding, layer, context.Assets, want, row);

        Assert.Empty(Compare(rejected));
        // A rejected row that resolved the wrong support, or names another layer, fails exactly on its address.
        Assert.Equal(new[] { "foliage-key" }, Compare(rejected with { Address = rejected.Address with { SupportOwnerId = Deck } }));
        Assert.Equal(new[] { "foliage-key" }, Compare(rejected with { Address = rejected.Address with { LayerId = Upper } }));
        Assert.Equal(new[] { "foliage-key" }, Compare(rejected with { Address = rejected.Address with { SupportOwnerId = null } }));
        // Only a refused point carries no support owner, so a refused row fails on its reason and not its address.
        Assert.Equal(new[] { "foliage-decision" }, Compare(rejected with { Reason = MapFoliageReason.Refused, Address = rejected.Address with { SupportOwnerId = null } }));
        Assert.Contains("foliage-key", Compare(rejected with { Reason = MapFoliageReason.Refused }));
        // Rows decided before support resolves still name the bound surface: off-raster and outside-domain rows of an
        // absent region, which the released Decide order stamps with the requested Surface.
        var absent = ShippedFoliageComparison.Actual(context, Cover, ShippedFoliageComparison.Area(new RegionCoord(0, 1)))!;
        Assert.Contains(absent, d => d.Reason == MapFoliageReason.OutsideDomain);
        Assert.Contains(absent, d => d.Reason == MapFoliageReason.RasterZero);
        Assert.All(absent, d => Assert.Equal((Cover, (string?)Ground), (d.Address.LayerId, d.Address.SupportOwnerId)));
    }

    [Fact]
    public void StaleInputAfterReleasedLoaders_IsRefusedByTheRecheck()
    {
        using var fixture = Create("valid");
        var entry = PrivateOracleInputs.Require(fixture.Environment);
        string region = Path.Combine(Source(fixture), "world/regions/r_0_0.json");
        var error = Assert.Throws<InvalidDataException>(() => ShippedFoliageInputs.Read(entry,
            afterLoaders: () => File.AppendAllText(region, fixture.PublicSecretMarker)));
        Assert.Equal("foliage extraction changed during load", error.Message);
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("missing-provenance")]
    [InlineData("missing-digest")]
    [InlineData("missing-report")]
    [InlineData("wrong-digest")]
    [InlineData("unattested-region")]
    [InlineData("unattested-catalog")]
    [InlineData("unattested-helper")]
    [InlineData("unattested-resource")]
    [InlineData("unattested-pack")]
    [InlineData("unattested-building")]
    [InlineData("duplicate-comparison")]
    [InlineData("case-comparison")]
    [InlineData("escape-path")]
    [InlineData("missing-owner")]
    [InlineData("extra-owner")]
    [InlineData("space-binding")]
    [InlineData("parent-link")]
    [InlineData("final-link")]
    [InlineData("unsafe-report")]
    [InlineData("existing-report")]
    [InlineData("linked-report")]
    public void IncompleteOrUnsafeClosure_RefusesWithoutDisclosingSyntheticSentinel(string variant)
    {
        using var fixture = Create(variant);
        var failure = Assert.ThrowsAny<Exception>(() => PrivateOracleEntry.Run(fixture.Environment, (inputs, report) =>
            ShippedFoliageComparison.Compare(ShippedFoliageInputs.Read(inputs), report)));
        Assert.True(failure is PrivateOracleInputException or PrivateOracleFailure, failure.GetType().Name);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.PublicSecretMarker, failure.ToString());
    }

    // Each variant passes the entry boundary and only the foliage reader refuses it, so the message names the rule.
    [Theory]
    [InlineData("duplicate-comparison", "duplicate or case-variant JSON member")]
    [InlineData("duplicate-manifest-field", "duplicate or case-variant JSON member")]
    [InlineData("case-duplicate-manifest-field", "duplicate or case-variant JSON member")]
    [InlineData("dangling-layer-owner", "dangling foliage layer owner")]
    [InlineData("building-manifest", "private foliage bindings are Surface only and take no building manifest")]
    [InlineData("patch-cells", "native support surface does not derive from its source plane")]
    [InlineData("case-manifest-field", "incomplete comparison object")]
    [InlineData("obsolete-rule-order", "incomplete comparison object")]
    [InlineData("unsupported-schema", "unsupported foliage comparison schema")]
    [InlineData("negative-expectation", "invalid foliage expectation")]
    [InlineData("escape-path", "unsafe attested foliage path")]
    [InlineData("unattested-generator", "unattested foliage dependency")]
    [InlineData("unattested-region", "unattested foliage dependency")]
    [InlineData("unattested-pack", "native tiled store does not verify")]
    [InlineData("wrong-generator", "missing retained generator")]
    [InlineData("duplicate-helper", "invalid comparison references")]
    [InlineData("wrong-convention", "unknown foliage candidate convention")]
    [InlineData("space-binding", "private foliage imports must bind a Surface")]
    [InlineData("tile-size", "foliage source terrain conversion requires metre tiles")]
    [InlineData("missing-owner", "incomplete foliage owner correspondence")]
    [InlineData("extra-owner", "incomplete foliage owner correspondence")]
    [InlineData("duplicate-owner", "duplicate foliage owner correspondence")]
    [InlineData("owner-rules", "foliage owner rule references differ")]
    [InlineData("unknown-rule", "unknown or missing foliage clearance rule")]
    [InlineData("model-row", "foliage model correspondence differs")]
    [InlineData("material-kind", "foliage material kind differs from the source catalog")]
    [InlineData("missing-material", "incomplete foliage material correspondence")]
    [InlineData("rule-footprint", "clearance rule disagrees with the source footprint")]
    [InlineData("rule-kind", "clearance rule disagrees with the source collision, roof or door")]
    public void ReaderOnlyRefusal_NamesItsSpecificReason(string variant, string reason)
    {
        using var fixture = Create(variant);
        var entry = PrivateOracleInputs.Require(fixture.Environment);
        var error = Assert.Throws<InvalidDataException>(() => ShippedFoliageInputs.Read(entry));
        Assert.Equal(reason, error.Message);
    }

    [Theory]
    [InlineData("world/regions/r_0_0.json")]
    [InlineData("catalog.json")]
    [InlineData("native.json")]
    [InlineData("native-tiled/map.json")]
    [InlineData("foliage.json")]
    [InlineData("generator/helper.py")]
    [InlineData("assets/mesh")]
    public void ChangedAfterOuterAttestation_IsRefusedBeforeComparison(string path)
    {
        using var fixture = Create("valid");
        var entry = PrivateOracleInputs.Require(fixture.Environment);
        File.AppendAllText(Path.Combine(Source(fixture), path), fixture.PublicSecretMarker);
        Assert.Throws<InvalidDataException>(() => ShippedFoliageInputs.Read(entry));
    }

    [Fact]
    public void CapturedNativeAndSourceBytes_SurviveLaterFileChanges()
    {
        using var fixture = Create("valid");
        var inputs = ShippedFoliageInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        foreach (string path in Directory.EnumerateFiles(Source(fixture), "*", SearchOption.AllDirectories))
            File.WriteAllText(path, fixture.PublicSecretMarker);
        using (var report = PrivateOracleReport.Open(Report(fixture)))
        {
            ShippedFoliageComparison.Compare(inputs, report);
            report.ThrowIfAnyFailed();
        }
        Assert.Empty(Summary(fixture).GetProperty("failures").EnumerateObject());
    }

    static string Source(ShippedFoliageGuardFixture fixture) => fixture.Environment["KHAOZ_R2_SHIPPED_SOURCE"]!;
    static string Report(ShippedFoliageGuardFixture fixture) => fixture.Environment["KHAOZ_R2_ORACLE_REPORT"]!;

    static JsonElement Summary(ShippedFoliageGuardFixture fixture) =>
        Lines(fixture).Single(l => l.GetProperty("category").GetString() == "foliage-summary").GetProperty("detail").Clone();

    static IReadOnlyList<string> Categories(ShippedFoliageGuardFixture fixture) =>
        Lines(fixture).Select(l => l.GetProperty("category").GetString()!).Where(c => c != "foliage-summary").ToArray();

    static IEnumerable<JsonElement> Lines(ShippedFoliageGuardFixture fixture) =>
        File.ReadAllLines(Report(fixture)).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
}
