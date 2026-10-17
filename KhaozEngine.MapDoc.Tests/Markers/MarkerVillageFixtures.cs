using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapDoc.Markers;

/// <summary>The invented public marker village. Linked into other test projects, so nothing here reads files.</summary>
internal static partial class MarkerFixtures
{
    internal const float VillageTileSize = 16;
    internal const string RoofLabelId = "a/entry";
    internal const string DisabledLocalId = "a/disabled-local";
    internal static readonly string[] VillageIds = { "a/disabled-local", "a/entry", "actor", "cave-bound", "cave-explicit", "start" };

    /// <summary>The room prefab's frame, northwest of the caves. Each support-bound point lies in storage slot (0, 0) of
    /// every world floor and of the prefab's local floor, with its 2 m seat scope inside that slot unless the slot's patch
    /// covers the point. The released seat query refuses with MissingGeometry when its scope captures a floor whose own
    /// slot at the point it did not read, and the authored parent therefore spans all four slots around the caves.</summary>
    internal static readonly MapTransform VillageFrame = new(new(-6, -450, 6), 0.371f, 1.2f);

    /// <summary>A complete native village with six uniquely named markers over released geometry only. The released room
    /// prefab sits at <see cref="VillageFrame"/> inside its authored world parent <c>exterior</c>, and the released
    /// stacked prefab-cave surfaces lie beside it. Its payload-1 marker child <c>entry</c> becomes the elevated
    /// <c>roof-label</c> through a replace override, and a payload-2 local child <c>disabled-local</c> is disabled,
    /// Explicit inside the prefab room and tagged second, first, first. World markers are <c>start</c>, Surface-bound to
    /// <c>exterior</c>, <c>actor</c>, Explicit with an archetype at negative X, <c>cave-explicit</c> in the deep room and
    /// <c>cave-bound</c>, Space-bound to the high cave. Every marker lies within the vertical extent of the world geometry.
    /// Roles are caller vocabulary. Neither legacy spawn list has a record.</summary>
    internal static MarkerFixture Village()
    {
        var disabledLocal = new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "disabled-local"),
            MapMarkerHeights.Apply(new(1, "disabled-local", "disabled-local", new Vector3(2, 0, -3), -0.5f, "actor-role", false,
                new[] { "second", "first", "first" }), new(1f, null)));
        MarkerFixture placed = Placed(VillageFrame, disabledLocal);
        MapDocument document = placed.Document;
        document.Id = "public-marker-village";
        document.TileSize = VillageTileSize;

        // The hut's payload-1 marker child keeps payload 1 and is renamed and raised above the roof by one override.
        MapPrefabMarkerChild entry = placed.Assets.GetPrefab("hut").Children.OfType<MapPrefabMarkerChild>().Single(c => c.Key.Key == "entry");
        var label = entry with { Value = new(1, "entry", "roof-label", new Vector3(2, 4.5f, -2), 0.25f, "label-role", true, new[] { "roof", "label" }) };
        MapPrefabInstance instance = document.Prefabs!.Instances.Single();
        document.Prefabs = new(1, 1, new[] { instance with { Overrides = new MapPrefabOverride[] { new MapPrefabReplaceOverride(entry.Key, label) } } }, []);

        // The released stacked caves keep their own surfaces, records and storeys.
        MapDocument caves = PrefabFixtures.Cave("stacked").Document;
        document.Surfaces.Refs.AddRange(caves.Surfaces.Refs);
        foreach (var (key, patch) in caves.Surfaces.Patches) document.Surfaces.Patches.Add(key, patch);
        document.BuildingIntent = new(1, [], [], [], VillageWorldMarkers())
        { Storeys = (document.BuildingIntent?.Storeys ?? []).Concat(caves.BuildingIntent!.Storeys).ToArray() };
        // Surface declarations in ordinal ID order, the order a tiled reload produces, so both storage forms save the same text.
        document.Surfaces.Refs.Sort((x, y) => string.CompareOrdinal(x.Id, y.Id));
        return new(MapDocumentFile.LoadText(MapDocumentFile.SaveText(document)), placed.Assets);
    }

    /// <summary>The four world markers of <see cref="Village"/> in their authored order.</summary>
    internal static MapLocalMarkerDoc[] VillageWorldMarkers() =>
    [
        MapMarkerHeights.Apply(new(1, "start", "start", new Vector3(10.5f, 0, -12.75f), 0.371f, "start-role", true, new[] { "first", "second" }),
            new(null, SurfaceBinding("exterior"))),
        MapMarkerHeights.Apply(new(1, "actor", "actor", new Vector3(-1.5f, 0, -10.25f), 1.25f, "actor-role", true, new[] { "actor" }),
            new(-451.25f, null)) with { ArchetypeId = "invented-actor" },
        MapMarkerHeights.Apply(new(1, "cave-explicit", "cave-explicit", new Vector3(2, 0, -2), 0.371f, "cave-role", true, new[] { "deep", "room" }),
            new(-57.75f, null)),
        MapMarkerHeights.Apply(new(1, "cave-bound", "cave-bound", new Vector3(2.5f, 0, -2.5f), -1.5f, "cave-role", true, new[] { "high" }),
            new(null, SpaceBinding("high-cave", -39.25f, 0.5f, 0.5f))),
    ];

    /// <summary><see cref="Village"/> with one legacy record in each list, as an old native document may still carry.
    /// Fixture construction writes them directly. No native editing path adds legacy records.</summary>
    internal static MarkerFixture VillageWithLegacyLists()
    {
        MarkerFixture village = Village();
        village.Document.Spawns.Add(new() { Id = "legacy-actor", ArchetypeId = "invented-actor", X = 10.5f, Z = -10.5f, Enabled = false, Tags = ["second", "first"] });
        village.Document.PlayerSpawns.Add(new() { Id = "legacy-start", X = 11.5f, Z = -8.5f, Yaw = 0.371f, Tags = ["first"] });
        return village;
    }

    /// <summary>Explicit adoption choices for <see cref="VillageWithLegacyLists"/>, one Explicit and one Surface-bound.</summary>
    internal static IReadOnlyDictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice> VillageChoices() =>
        new Dictionary<MapLegacySpawnKey, MapMarkerAdoptionChoice>
        {
            [new(MapLegacySpawnKind.Spawn, "legacy-actor")] = new("adopted-actor", "adopted-actor", "actor-role", new(-451.5f, null)),
            [new(MapLegacySpawnKind.PlayerSpawn, "legacy-start")] = new("adopted-start", "adopted-start", "start-role", new(null, SurfaceBinding("exterior"))),
        };

    /// <summary>The closed wire form of <see cref="VillageChoices"/>.</summary>
    internal const string VillageChoicesJson = """
        [{"kind":"Spawn","id":"legacy-actor","markerId":"adopted-actor","name":"adopted-actor","role":"actor-role","height":{"explicitY":-451.5}},
         {"kind":"PlayerSpawn","id":"legacy-start","markerId":"adopted-start","name":"adopted-start","role":"start-role","height":{"supportBinding":{"kind":"Surface","surfaceId":"exterior"}}}]
        """;

    /// <summary>A local point through <paramref name="frame"/>, computed independently of the product kernel: a binary64
    /// yaw and uniform scale, then the 2^-20 metre freeze on X and Z, and the frozen Y narrowed to float.</summary>
    internal static MapExactPoint FramePoint(MapTransform frame, float x, float y, float z)
    {
        var (sin, cos) = Math.SinCos((double)frame.YawRadians);
        double scale = frame.Scale;
        double worldX = frame.Position.X + scale * (cos * x + sin * z), worldZ = frame.Position.Z + scale * (-sin * x + cos * z);
        double worldY = Math.Round((frame.Position.Y + scale * y) * 1048576, MidpointRounding.ToEven) / 1048576;
        return new(Frozen(worldX), MapExactValue.FromSingle((float)worldY), Frozen(worldZ));
    }

    static MapExactValue Frozen(double metres) => new(checked((long)Math.Round(metres * 1048576, MidpointRounding.ToEven)), 1048576);

    /// <summary>True when two yaws agree modulo a turn within the shared 0.000001 radian tolerance.</summary>
    internal static bool SameYaw(double a, double b) => Math.Abs(Math.IEEERemainder(a - b, 2 * Math.PI)) <= 0.000001;
}
