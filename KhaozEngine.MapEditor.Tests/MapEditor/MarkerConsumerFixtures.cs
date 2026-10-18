using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Tests.MapDoc;
using KhaozEngine.Tests.MapDoc.Markers;
using KhaozEngine.Tests.MapDoc.Prefabs;

namespace KhaozEngine.Tests.MapEditor;

/// <summary>Editor-only public invented fixtures for marker consumers: a tiled resolver-2 world with its closure beside
/// it, whose player start lies two storage tiles west of the bounds centre.</summary>
internal static class MarkerConsumerFixtures
{
    internal const string StartRole = "start-role";
    internal const string SupportOwner = "adoption-floor";
    internal const string UpperFloor = "upper-floor";

    internal static MapMarkerConsumerRoles Roles() => new(StartRole, "spawn-role", "landmark-role");

    /// <summary>Writes a valid tiled native world at <paramref name="root"/>, with bounds centred on the origin and tile
    /// size 64. Prefab instance <c>start-frame</c> at (-64, 0, 0.5) holds a payload-2 enabled start <c>start-a</c>,
    /// Surface-bound to the world floor <c>adoption-floor</c> at local (-0.25, 0), so its exact world X is -64.25 in
    /// storage tile -2. A nearer disabled start <c>start-disabled</c> and an enabled standalone start <c>start-tie</c>
    /// mirrored across the centre at the same distance share the role. <c>adoption-floor</c> lies at Y 20 under the start
    /// and around the origin, <c>upper-floor</c> lies at Y 70 over the origin, and the analytic field is a flat 900. The
    /// origin holds the Surface-bound placement <c>bound</c> and both unadopted legacy lists, disabled, so the released
    /// player spawn search finds nothing. The prefab also holds an explicit-height placement child, bound as
    /// <c>prefab/start-frame/Placement/post</c> by the released planner, at world (-63.5, 3, 0). Two occupied storage tiles put the world over a tile limit of one.
    /// Variant <c>enabled-legacy-start</c> enables the legacy player spawn in storage tile (-1, -1). Variant
    /// <c>uncovered-spawn</c> adds an enabled legacy NPC spawn <c>uncovered</c> at (10.5, 10.5), outside every floor.</summary>
    internal static string WindowedNative(string root, string variant = "basic")
    {
        if (variant is not ("basic" or "enabled-legacy-start" or "uncovered-spawn"))
            throw new ArgumentException("Unknown marker consumer variant " + variant, nameof(variant));
        var set = new MapSurfaceSet();
        set.Refs.Add(MixedResolutionFixtures.Surface(SupportOwner, 1, MapSurfaceRole.SupportFloor));
        set.Refs.Add(MixedResolutionFixtures.Surface(UpperFloor, 1, MapSurfaceRole.SupportFloor));
        foreach (int x in new[] { -1, 0 })
            foreach (int z in new[] { -1, 0 })
            {
                AddPatch(set, SupportOwner, x, z, 2000);
                AddPatch(set, UpperFloor, x, z, 7000);
            }
        AddPatch(set, SupportOwner, -2, 0, 2000);
        MapDocument document = MixedResolutionFixtures.Document(set);
        document.Id = "marker-consumer-window";
        document.Bounds = new() { MinX = -256, MinZ = -256, MaxX = 256, MaxZ = 256 };
        document.PlayableBounds = new() { MinX = -256, MinZ = -256, MaxX = 256, MaxZ = 256 };
        document.Terrain.GentleAmplitude = 0;
        document.Terrain.Biomes.Add(new MapBiomeBand { BaseHeight = 900 });

        var start = new MapPrefabMarkerChild(new(MapPrefabChildKind.Marker, "start"),
            MapMarkerHeights.Apply(new(1, "start", "start-a-name", new Vector3(-0.25f, 0, 0), 0.5f, StartRole, true, new[] { "arrival" }),
                new(null, MarkerFixtures.SurfaceBinding(SupportOwner))))
        { References = [new("/value/supportBinding/surfaceId", new(MapPrefabReferenceScope.World, MapPrefabChildKind.Surface, SupportOwner))] };
        var post = new MapPrefabPlacementChild(new(MapPrefabChildKind.Placement, "post"),
            new MapPlacement { Id = "post", Kind = "item", AssetId = "item", X = 0.5f, Y = 3, Z = -0.5f, Yaw = 0.25f, Scale = 1, Tags = ["edge"] });
        var asset = new MapPrefabAsset(1, "hut", new MapPrefabChild[] { start, post });
        MapAssetClosure assets = PrefabFixtures.LoadAssets(MapPrefabFile.Write(asset), true);
        document.NativeAssets = assets.Roots.ToList();
        var instance = new MapPrefabInstance("start-frame", "hut", assets.GetResource("hut").Reference.Sha256,
            new(new Vector3(-64, 0, 0.5f), 0, 1), null, new(Array.Empty<MapPrefabBinding>()), []);
        document.Prefabs = new(1, 1, new[] { instance }, []);
        MapPrefabBindingPlan plan = MapPrefabBindingPlanner.Plan(document, asset, assets, instance.Id, instance.Bindings);
        // The marker child takes the start's own world ID. The planner names the placement child.
        var bindings = new MapPrefabBindings(plan.Bindings.Entries.Select(b => b.Child == start.Key ? b with { WorldId = "start-a" } : b).ToArray());
        document.Prefabs = new(1, 1, new[] { instance with { Bindings = bindings } }, []);
        document.NumericIdHighWaterMark = plan.HighWaterMark;
        document.BuildingIntent = new(1, [], [], [], new[]
        {
            Explicit("start-disabled", 0.25f, 0.25f) with { Enabled = false },
            Explicit("start-tie", 64.25f, 0.5f),
        });
        document.Placements.Add(new MapPlacement
        {
            Id = "bound",
            Kind = "item",
            AssetId = "item",
            X = 0.5f,
            Z = 0.5f,
            Scale = 1,
            SupportBinding = MarkerFixtures.SurfaceBinding(SupportOwner),
        });
        document.Spawns.Add(new() { Id = "old", ArchetypeId = "kit.actor", X = -0.25f, Z = -1.5f, Enabled = false, Tags = ["second", "first"] });
        document.PlayerSpawns.Add(new() { Id = "old", X = -0.25f, Z = -1.5f, Yaw = 0.371f, Enabled = variant == "enabled-legacy-start", Tags = ["second", "first"] });
        if (variant == "uncovered-spawn")
            document.Spawns.Add(new() { Id = "uncovered", ArchetypeId = "kit.actor", X = 10.5f, Z = 10.5f, Tags = ["far"] });
        MapBoundDocumentValidation.Validate(document, assets);

        MapDocumentFile.SaveTiled(document, root);
        foreach (MapResolvedResource resource in assets.CapturedResources)
        {
            string target = Path.Combine(root, resource.Reference.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, resource.Bytes.ToArray());
        }
        return root;
    }

    /// <summary>Overwrites every terrain tile body and every support page under <paramref name="root"/> with invalid bytes,
    /// leaving the manifest, the surface index and directory, and the closure intact. Returns the total.</summary>
    internal static int CorruptBodies(string root) => CorruptBodies(root, out _, out _);

    /// <summary>As <see cref="CorruptBodies(string)"/>, also reporting the terrain tile bodies and support pages apart.</summary>
    internal static int CorruptBodies(string root, out int terrainBodies, out int supportPages)
    {
        byte[] invalid = Encoding.UTF8.GetBytes("not a tile body");
        terrainBodies = 0;
        supportPages = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "tiles"), "*.json", SearchOption.AllDirectories))
        {
            bool tile = Path.GetFileName(file).StartsWith("t_", StringComparison.Ordinal);
            // Support pages live at tiles/surfaces/p/<two hex>/<digest>.json.
            bool page = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file))) == "p";
            if (!tile && !page) continue;
            File.WriteAllBytes(file, invalid);
            if (tile) terrainBodies++;
            else supportPages++;
        }
        return terrainBodies + supportPages;
    }

    /// <summary>The closure of <paramref name="manifest"/>, verified under the tiled storage root.</summary>
    internal static MapAssetClosure Closure(MapDocument manifest, string root) =>
        MapAssetClosure.Load(manifest.NativeAssets, new MapStorageGuardedAssetSource(Path.GetFullPath(root), MapDocumentForm.Tiled));

    /// <summary>The released bounds centre in exact form.</summary>
    internal static MapExactXz BoundsCentre(MapDocument manifest) => new(
        MapExactValue.FromSingle((manifest.Bounds.MinX + manifest.Bounds.MaxX) * 0.5f),
        MapExactValue.FromSingle((manifest.Bounds.MinZ + manifest.Bounds.MaxZ) * 0.5f));

    /// <summary>A fresh directory under the test output, removed by the caller.</summary>
    internal static string TempRoot() => Path.Combine(Path.GetTempPath(), "ke-marker-consumer-" + Guid.NewGuid().ToString("N"));

    static MapLocalMarkerDoc Explicit(string id, float x, float z) =>
        MapMarkerHeights.Apply(new(1, id, id + "-name", new Vector3(x, 0, z), 0, StartRole, true, Array.Empty<string>()), new(20, null));

    // A 2 by 2 metre patch in the corner of storage patch (x, z) that touches the origin, or the start's corner for x -2.
    static void AddPatch(MapSurfaceSet set, string surface, int x, int z, int centimetres)
    {
        MapSurfacePatch patch = MixedResolutionFixtures.Patch(new(surface, x, z), 2, 2, (_, _) => centimetres);
        patch.CellMinX = x < 0 ? 62 : 0;
        patch.CellMinZ = z < 0 ? 62 : 0;
        set.Patches.Add(patch.Key, patch);
    }
}
