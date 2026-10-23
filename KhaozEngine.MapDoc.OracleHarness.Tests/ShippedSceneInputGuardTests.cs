using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Interiors;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed partial class ShippedSceneInputGuardTests
{
    [Fact]
    public void SceneInputs_RequireEveryDependencyAndCorrespondence()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        Assert.NotNull(inputs.Manifest);
        PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.Compare(inputs, report));
        int validSyntheticFailures = fixture.FailureCategories().Count;
        Assert.Equal(0, validSyntheticFailures);
    }

    [Theory]
    [InlineData("foliage-valid")]
    [InlineData("water-valid")]
    [InlineData("light-valid")]
    public void SceneInputs_NonemptyCategoriesUseActualPublishedRecording(string variant)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant);
        try { PrivateOracleEntry.Run(fixture.Environment, (inputs, report) => ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report)); }
        catch (PrivateOracleFailure)
        {
            // These fixtures are authored in this file. Never use this diagnostic path with private inputs.
            var rows = File.ReadAllLines(fixture.ReportPath).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
            string message = string.Join("\n", rows.Where(r => r.GetProperty("category").GetString() != "scene-summary").Take(3).Select(r => r.GetRawText())).Replace(fixture.SourceRoot, "<fixture>", StringComparison.Ordinal)
                .Replace(fixture.PublicSecretMarker, "<sentinel>", StringComparison.Ordinal);
            Assert.Fail(message);
        }
        Assert.Empty(fixture.FailureCategories());
    }

    [Theory]
    [InlineData("missing-part", "scene-placement-part")]
    [InlineData("duplicate-resource", "scene-resource-duplicate")]
    [InlineData("changed-mesh", "scene-placement-part")]
    [InlineData("changed-roof", "scene-roof-channel")]
    [InlineData("changed-water", "scene-water")]
    [InlineData("changed-foliage", "scene-foliage")]
    public void SceneDrawSet_RejectsMissingDuplicateOrChangedResource(string variant, string category)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var expected = ShippedSceneGuardDraws.Create();
        var changed = ShippedSceneGuardDraws.Mutate(expected, variant);
        var failure = Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.CompareDrawSets(expected, changed, report)));
        Assert.Contains(category, fixture.FailureCategories());
        Assert.DoesNotContain(fixture.PublicSecretMarker, failure.ToString());
    }

    [Fact]
    public void ScenePolicy_ClosedSerializationPreservesColourAndStableProfiles()
    {
        var colour = new KhaozEngine.Primitives.Color(.2f, .3f, .4f, 1);
        var options = new KhaozEngine.Terrain.GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = KhaozEngine.Terrain.GroundCoverFadeMode.HeightScale,
            MotionProfiles = new System.Collections.Generic.Dictionary<string, KhaozEngine.Render3D.FoliageMotionProfile>
            { ["sprig"] = new(1, .2f, 2, .1f) }
        };
        var json = JsonSerializer.SerializeToElement(new
        {
            placements = new MapPlacementPolicy([], 100),
            animatedAssets = Array.Empty<string>(),
            foliage = options,
            water = new KhaozEngine.Render3D.WaterLook { DeepColor = colour },
            lights = Array.Empty<SceneSourceLight>(),
            modelExtents = Array.Empty<KhaozEngine.MapDoc.Foliage.MapFoliageModelExtent>()
        }, ShippedScenePolicy.Json);
        var read = ShippedScenePolicy.Read(json);
        Assert.Equal(2, read.Foliage.MotionProfiles["sprig"].WindScale);
        Assert.Equal(colour, read.Water.DeepColor);
    }

    [Fact]
    public void SceneDrawSet_RejectsCompletenessIndependently()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var witness = new MapSceneWitness("closure", "source", "scope", [new("Placement", "one")], [], [], [], true, false);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment, (_, report) => ShippedSceneComparison.CheckCoverage(witness, report)));
        Assert.Contains("scene-completeness", fixture.FailureCategories());
    }

    [Theory]
    [InlineData("generator")]
    [InlineData("material")]
    [InlineData("placement")]
    [InlineData("lod")]
    [InlineData("window")]
    [InlineData("sequence")]
    [InlineData("policy")]
    [InlineData("probe")]
    [InlineData("expectations")]
    public void SceneInputs_RejectUnknownMissingAndCaseVariantNestedMembers(string row)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        string json = PrivateOracleInputs.Require(fixture.Environment).Provenance.Comparison.GetRawText();
        foreach (string mutation in new[] { "unknown", "missing", "case", "duplicate" })
        {
            var root = JsonNode.Parse(json)!.AsObject();
            var target = row switch
            {
                "generator" => root["generator"],
                "material" => root["materials"]![0],
                "placement" => root["placements"]![0],
                "lod" => root["placements"]![0]!["lods"]![0],
                "window" => root["windows"]![0],
                "sequence" => root["sequence"]![0],
                "policy" => root["policy"],
                "probe" => root["policy"]!["probes"]![0],
                "expectations" => root["expectations"],
                _ => throw new InvalidOperationException()
            };
            var obj = target!.AsObject(); string first = obj.First().Key;
            if (mutation == "unknown") obj["unknown"] = true;
            if (mutation == "missing") obj.Remove(first);
            if (mutation == "case") { obj[char.ToUpperInvariant(first[0]) + first[1..]] = obj[first]!.DeepClone(); obj.Remove(first); }
            if (mutation == "duplicate") obj[char.ToUpperInvariant(first[0]) + first[1..]] = obj[first]!.DeepClone();
            using var changed = JsonDocument.Parse(root.ToJsonString(ShippedScenePolicy.Json));
            Assert.Throws<InvalidDataException>(() => ShippedSceneManifest.Read(changed.RootElement));
        }
    }

    [Fact]
    public void SceneAutoRoof_UsesOriginalIndoorFillAndObserverFloor()
    {
        var world = new KhaozEngine.TileWorld.TileWorldDocument { PlaneCount = 2 };
        world.GetOrCreateRegion(new(0, 0));
        world.SetUnderlay(0, 0, 0, 1);
        world.SetSettings(0, 0, 0, KhaozEngine.TileWorld.TileSettings.Indoors);
        var probe = new SceneProbe("inside", "A", .5, 1, -.5, MapRoofMode.Auto, 0, 0, 0, 0, 0);
        Assert.True(LegacySceneRecorder.RoofHidden(world, new(0, 0, 1, 1), 1, probe));
        Assert.False(LegacySceneRecorder.RoofHidden(world, new(0, 0, 1, 1), 0, probe));
        Assert.False(LegacySceneRecorder.RoofHidden(world, new(0, 0, 1, 1), 1, probe with { ObserverX = 2 }));
    }

    [Theory]
    [InlineData("native-mesh", "scene-placement-part")]
    [InlineData("native-transform", "scene-placement-frame")]
    public void NativeMutations_LeaveIndependentSourceInventoryUnchanged(string variant, string category)
    {
        using var normal = ShippedSceneGuardFixtures.Create("valid");
        using var changed = ShippedSceneGuardFixtures.Create(variant);
        var first = ShippedSceneInputs.Read(PrivateOracleInputs.Require(normal.Environment));
        var second = ShippedSceneInputs.Read(PrivateOracleInputs.Require(changed.Environment));
        var a = LegacySceneRecorder.Record(first.World, first.Catalogs, first);
        var b = LegacySceneRecorder.Record(second.World, second.Catalogs, second);
        Assert.Equal(a.Placements[0].World, b.Placements[0].World);
        Assert.True(ShippedSceneComparison.MeshEqual(a.Placements[0].Tiers[0][0].Mesh, b.Placements[0].Tiers[0][0].Mesh));
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(changed.Environment, (_, report) => ShippedSceneComparison.Compare(second, report)));
        Assert.Contains(category, changed.FailureCategories());
    }

    [Theory]
    [InlineData("dissolve", "scene-rigid-frame")]
    [InlineData("complement", "scene-rigid-frame")]
    [InlineData("shadow-inversion", "scene-rigid-frame")]
    [InlineData("light-colour", "scene-light-frame")]
    [InlineData("light-radius", "scene-light-frame")]
    [InlineData("light-intensity", "scene-light-frame")]
    [InlineData("light-exclusion", "scene-light-frame")]
    [InlineData("swell", "scene-water-frame")]
    [InlineData("wind", "scene-foliage-frame")]
    [InlineData("interactor", "scene-foliage-frame")]
    [InlineData("foliage-shadow", "scene-foliage")]
    public void SceneTypedFrames_RejectPolicyOnlyMutations(string variant, string category) => Reject(variant, category);

    [Theory]
    [InlineData("model-indices")]
    [InlineData("model-parts")]
    [InlineData("model-profile")]
    [InlineData("model-casting")]
    public void SceneR11_BindingAndStableRegistrationAreCompared(string variant) => Reject(variant, "scene-foliage");

    [Fact]
    public void SceneR12_ExplicitLocalFocusIsAnActualFrameInput() => Reject("focus", "scene-focus");

    static void Reject(string variant, string category)
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var expected = ShippedSceneGuardDraws.Create();
        var changed = ShippedSceneGuardDraws.Mutate(expected, variant);
        Assert.Throws<PrivateOracleFailure>(() => PrivateOracleEntry.Run(fixture.Environment,
            (_, report) => ShippedSceneComparison.CompareDrawSets(expected, changed, report)));
        Assert.Contains(category, fixture.FailureCategories());
        Assert.All(changed.Resources, r => Assert.Equal("invented-payload", r.PayloadDigest));
    }

    [Fact]
    public void SceneStreaming_RejectsUnequalDuplicateAndLateResurrection()
    {
        using var fixture = ShippedSceneGuardFixtures.Create("valid");
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var document = inputs.Reload(false); var assets = inputs.Assets(document);
        using var docs = MapDocumentSource.FromDocument(document);
        var source = MapSceneSource.Capture(docs, assets).Source!;
        var proofs = MapRenderExtentValidator.Validate(assets, source.RequiredAssetIds, new(new())).Proofs!;
        source = source.WithRenderExtents(proofs).Source!;
        var policy = inputs.NativePolicy.Native(assets, inputs.Manifest);
        var state = new MapRenderSourceState(source, assets, proofs, policy, [ShippedSceneInputs.Scope(inputs.Manifest.Windows[0])]);
        var scheduler = new SceneOracleScheduler(); var device = new SceneOracleDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ShippedSceneComparison.Complete(residency, scheduler);
        var first = new MapDrawRecorder(); var point = ShippedSceneInputs.Point(0, 2, 0);
        residency.Record(new(default, point, MapRoofMode.AlwaysVisible, 1, 17, point), first);
        var old = first.Snapshot(); Assert.NotEmpty(old.Resources);
        var replacement = state with { Policy = new(new([], 150), policy.AnimatedAssets, policy.Foliage, policy.Water, policy.MaterialResourceIds, policy.TerrainMaterials) };
        residency.RequestReplacement(replacement); ShippedSceneComparison.Complete(residency, scheduler);
        Assert.NotEqual(old.Resources[0].Reference.Stamp.PolicyDigest, residency.AcceptedPlans[0].Key.PolicyDigest);
        using (var report = PrivateOracleReport.Open(fixture.ReportPath))
        {
            ShippedSceneComparison.CheckStamp(old, residency.AcceptedPlans[0].Key, report);
            Assert.Throws<PrivateOracleFailure>(report.ThrowIfAnyFailed);
        }
        Assert.Contains("scene-generation", fixture.FailureCategories());
        residency.SetWindows([]); ShippedSceneComparison.Complete(residency, scheduler);
        Assert.Empty(device.Live);
        var final = new MapDrawRecorder(); residency.Record(new(default, point, MapRoofMode.AlwaysVisible, 1, 17, point), final);
        Assert.Empty(final.Snapshot().Resources);
        using var waterFixture = ShippedSceneGuardFixtures.Create("valid");
        PrivateOracleEntry.Run(waterFixture.Environment, (_, report) =>
            ShippedSceneComparison.CompareDrawSets(ShippedSceneGuardDraws.Create(), ShippedSceneGuardDraws.Create(), report));
        Reject("changed-water", "scene-water");
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("missing-provenance")]
    [InlineData("missing-digest")]
    [InlineData("missing-report")]
    [InlineData("wrong-digest")]
    [InlineData("unsafe-report")]
    [InlineData("existing-report")]
    [InlineData("linked-report")]
    [InlineData("unattested-helper")]
    [InlineData("unattested-pack")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("case-field")]
    [InlineData("missing-field")]
    [InlineData("stale-source")]
    [InlineData("unapproved-rule")]
    public void SceneGuard_SanitizesAllPrivateFailureRoutes(string variant)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(variant);
        var failure = Assert.ThrowsAny<Exception>(() => PrivateOracleEntry.Run(fixture.Environment,
            (inputs, report) => ShippedSceneComparison.Compare(ShippedSceneInputs.Read(inputs), report)));
        Assert.True(failure is PrivateOracleInputException or PrivateOracleFailure);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain(fixture.PublicSecretMarker, failure.ToString());
    }
}
