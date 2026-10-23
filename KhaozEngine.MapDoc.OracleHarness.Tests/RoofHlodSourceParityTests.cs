using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.MapDocOracle;

[UnsupportedOSPlatform("windows")]
public sealed class RoofHlodSourceParityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TwoIndependentHouses_CompareSourceDrivenDrawSetsAcrossPartialWindows(bool shadows)
    {
        using var fixture = ShippedSceneGuardFixtures.Create(shadows ? "roof-hlod" : "roof-hlod-no-shadows", comparison =>
        {
            var probes = new JsonArray();
            foreach (float distance in new[] { 0f, 30f, 40f })
                foreach (var (id, x, y, mode) in new[] { ("house-a", 2.5f, 1f, "Auto"), ("house-b", 10.5f, 1f, "Auto"),
                    ("outdoors", 20f, 1f, "Auto"), ("above", 2.5f, 4f, "Auto"), ("hidden", 2.5f, 1f, "AlwaysHidden"), ("visible", 2.5f, 1f, "AlwaysVisible") })
                    probes.Add(new JsonObject { ["id"] = id + distance, ["windowId"] = "A", ["observerX"] = x, ["observerY"] = y,
                        ["observerZ"] = -2.5, ["roofMode"] = mode, ["timeSeconds"] = 1, ["windSeed"] = 17,
                        ["focusX"] = 32 + distance, ["focusY"] = 2, ["focusZ"] = -32 });
            comparison["policy"]!["probes"] = probes;
        });
        var inputs = ShippedSceneInputs.Read(PrivateOracleInputs.Require(fixture.Environment));
        var world = new TileWorldDocument { Id = "invented-two-houses", PlaneCount = 2 };
        world.GetOrCreateRegion(new(0, 0));
        for (int x = 0; x < 4; x++) for (int z = 0; z < 4; z++) world.SetUnderlay(x, z, 0, 1);
        world.AddObject("column", 2, 2, 1, 0); world.AddObject("column", 10, 2, 1, 0);
        foreach (int x in new[] { 2, 10 })
        {
            world.SetUnderlay(x, 2, 0, 1); world.SetSettings(x, 2, 0, TileSettings.Indoors);
            world.SetUnderlay(x, 2, 1, 1);
            for (int a = x; a <= x + 1; a++) for (int z = 2; z <= 3; z++) world.SetCornerHeightCm(a, z, 1, 300);
        }
        var document = inputs.Reload(false); var assets = inputs.Assets(document);
        foreach (var placement in document.Placements) placement.Y = 3;
        var converted = LegacyOracleConverter.ToNative(world, new(0, 0), 0);
        var patch = converted.Patch;
        var heights = new int[13 * 5]; var cells = new MapSurfaceCell[12 * 4];
        for (int z = 0; z <= 4; z++) for (int x = 0; x <= 12; x++) heights[z * 13 + x] = patch.Heights[z * 65 + x];
        for (int z = 0; z < 4; z++) for (int x = 0; x < 12; x++) cells[z * 12 + x] = patch.Cells[z * 64 + x];
        patch.Width = 12; patch.Depth = 4; patch.Heights = heights; patch.Cells = cells; patch.Presence = [(1UL << 48) - 1];
        patch.Records.Clear();
        var parent = new MapRecordRef("outside", patch.Key);
        patch.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
        patch.Records.Add(new MapSpaceFootprint("outside-domain", parent, patch.Key,
            Enumerable.Range(0, 4).SelectMany(z => Enumerable.Range(0, 12).Where(x => x < 4 || (x == 10 && z == 2)).Select(x => z * 64 + x)).ToArray(),
            new(MapBoundKind.SupportFloor, "plane-0", null), new(MapBoundKind.OpenTop, null, null)));
        foreach (var (room, x) in new[] { ("a", 2), ("b", 10) })
        {
            patch.Records.Add(new MapSpaceDoc(room, MapSpaceKind.Room, parent, null, [], [], [], []));
            patch.Records.Add(new MapPrismDomain(room + "-domain", 1, new(room, patch.Key), parent, "", "floor",
                new(MapLatticeFrame.ImportedMetreCentimetre, [new(x, 2, x + 1, 3)], []), 0, 3));
        }
        document.Surfaces = new(); document.Surfaces.Patches.Add(patch.Key, patch);
        document.Surfaces.Refs.Add(converted.Surface with { SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(converted.Surface,
            [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))]) });
        MapPrefabReference Ref(MapPrefabChildKind kind, string id) => new(MapPrefabReferenceScope.World, kind, id);
        document.BuildingIntent = new(1,
            [new("a-link", Ref(MapPrefabChildKind.Placement, "native-placement"), [Ref(MapPrefabChildKind.Record, "a")], [], 3),
             new("b-link", Ref(MapPrefabChildKind.Placement, "native-second"), [Ref(MapPrefabChildKind.Record, "b")], [], 3)], [], [], [])
        { Roofs = [Ref(MapPrefabChildKind.Placement, "native-placement"), Ref(MapPrefabChildKind.Placement, "native-second")], Storeys = [new(1, "floor", "Floor", null)] };
        using var documents = MapDocumentSource.FromDocument(document);
        var source = MapSceneSource.Capture(documents, assets).Source!;
        var proof = MapRenderExtentValidator.Validate(assets, source.RequiredAssetIds, new(new())).Proofs!;
        source = source.WithRenderExtents(proof).Source!;
        var windows = inputs.Manifest.Windows.Take(2).Select(ShippedSceneInputs.Scope).ToArray();
        var scheduler = new SceneOracleScheduler(); var device = new SceneOracleDevice();
        using var residency = new MapRenderResidency(new(source, assets, proof, inputs.NativePolicy.Native(assets, inputs.Manifest), windows), device, null, scheduler);
        ShippedSceneComparison.Complete(residency, scheduler);
        var expected = LegacySceneRecorder.Record(world, inputs.Catalogs, inputs);
        try
        {
            PrivateOracleEntry.Run(fixture.Environment, (_, report) =>
            {
                foreach (var probe in inputs.Manifest.Policy.Probes)
                {
                    var sourceFrame = expected.Frames.Single(f => f.ProbeId == probe.Id);
                    var observer = ShippedSceneInputs.Point(probe.ObserverX, probe.ObserverY, probe.ObserverZ);
                    var sink = new MapDrawRecorder(); residency.Record(new(observer.Frame, observer, probe.RoofMode, 1, 17, sourceFrame.Focus), sink);
                    var actual = sink.Snapshot(); actual = new(actual.Resources, actual.Draws.Where(d => d.Resource.Key.Kind == MapRenderKind.Roof).ToArray());
                    var rows = ShippedSceneExpectedFrames.Capture(inputs, expected, sourceFrame, probe, windows[0], [], new Dictionary<MapRenderKey, MapSubmissionAnchor>(), new Dictionary<MapRenderKey, MapSubmissionAnchor>());
                    ShippedSceneComparison.CompareDrawSets(rows, actual, report);
                }
            });
        }
        catch (PrivateOracleFailure) { Assert.Fail(string.Join(", ", fixture.FailureCategories().Distinct())); }
        Assert.Empty(fixture.FailureCategories());
    }
}
