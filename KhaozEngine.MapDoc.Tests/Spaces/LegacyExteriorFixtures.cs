using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc;

internal static class LegacyExteriorFixtures
{
    internal static MapDocument Row(string variant, long slotX = 0)
    {
        if (variant is not ("tagged" or "untagged" or "hole" or "cave" or "upper-tag" or "foreign-lattice"))
            throw new ArgumentException("unknown legacy exterior fixture variant", nameof(variant));
        var set = new MapSurfaceSet();
        set.Refs.Add(new("plane-0", MapLatticeFrame.ImportedMetreCentimetre,
            MapSurfaceRole.SupportFloor, MapPresencePolicy.LegacyTileWorld, null, null, ""));
        MapSurfacePatch floor = MixedResolutionFixtures.Patch(new("plane-0", slotX, 0), 4, 1, (_, _) => 0);
        floor.Heights = new[] { 0, 100, 300, 300, 300, 0, 200, 600, 600, 600 };
        floor.Cells[1] = floor.Cells[1] with { Flags = MapCellFlags.NoDraw };
        floor.Cells[2] = floor.Cells[2] with { Underlay = 0 };
        floor.SetPresent(3, 0, false);
        set.Patches.Add(floor.Key, floor);

        var lower = new MapBoundRef(variant == "untagged" ? MapBoundKind.SupportFloor : MapBoundKind.LegacyExteriorV1,
            "plane-0", null);
        var upper = new MapBoundRef(MapBoundKind.OpenTop, null, null);
        MapPatchKey lattice = floor.Key;
        MapSpaceKind kind = MapSpaceKind.Exterior;
        if (variant == "cave")
        {
            kind = MapSpaceKind.Cave;
            upper = new(MapBoundKind.Ceiling, "roof", null);
            AddNativeRow(set, "roof", slotX, MapSurfaceRole.Ceiling, 900);
        }
        if (variant == "upper-tag") upper = new(MapBoundKind.LegacyExteriorV1, "plane-0", null);
        if (variant == "foreign-lattice")
        {
            AddNativeRow(set, "shadow", slotX, MapSurfaceRole.SupportFloor, -1000);
            lattice = new("shadow", slotX, 0);
        }
        var space = new MapSpaceDoc("world", kind, null, null, Array.Empty<string>(),
            Array.Empty<MapBoundaryRef>(), Array.Empty<MapBoundaryRef>(), Array.Empty<MapRecordRef>());
        var footprint = new MapSpaceFootprint("world-cells", new("world", floor.Key), lattice,
            variant == "hole" ? new[] { 0, 1, 2, 3 } : new[] { 0, 1, 2 }, lower, upper);
        floor.Records.Add(space);
        floor.Records.Add(footprint);
        RequireVariant(set, footprint, space, variant);
        return MixedResolutionFixtures.Document(set);
    }

    internal static MapScopedSurfaces View(string variant) => MapScopedSurfaces.CompleteView(Row(variant).Surfaces);

    internal static MapSpaceFootprint Footprint(MapScopedSurfaces view)
        => view.Witness.Present.Where(p => p.Key.SurfaceId == "plane-0")
            .SelectMany(p => view.RecordsIn(p.Key)).OfType<MapSpaceFootprint>().Single();

    internal static MapCompiledPatch Compiled(MapDocument document)
        => MapSurfaceCompiler.Compile(document.Surfaces.Refs.Single(s => s.Id == "plane-0"),
            document.Surfaces.Patches.Values.Single(p => p.Key.SurfaceId == "plane-0"));

    static void AddNativeRow(MapSurfaceSet set, string id, long slotX, MapSurfaceRole role, int height)
    {
        set.Refs.Add(new(id, new(new(1, 1), new(1, 100), MapRowDirection.NegativeZ, MapHeightDatum.WorldY0),
            role, MapPresencePolicy.Native, null, null, ""));
        MapSurfacePatch patch = MixedResolutionFixtures.Patch(new(id, slotX, 0), 3, 1, (_, _) => height);
        set.Patches.Add(patch.Key, patch);
    }

    static void RequireVariant(MapSurfaceSet set, MapSpaceFootprint footprint, MapSpaceDoc space, string variant)
    {
        foreach (MapSurfacePatch patch in set.Patches.Values)
        {
            IReadOnlyList<string> local = patch.ValidateLocal();
            if (local.Count != 0)
                throw new InvalidOperationException("legacy exterior fixture local validation failed: " + string.Join(", ", local));
        }
        string? expectedRecipe = variant switch
        {
            "cave" => "legacy recipe: space",
            "upper-tag" => "legacy recipe: upper",
            "foreign-lattice" => "legacy recipe: lattice",
            _ => null,
        };
        string? recipe = MapLegacyExteriorRecipe.Check(footprint, space, set.Refs.Single(s => s.Id == "plane-0"));
        if (recipe != expectedRecipe)
            throw new InvalidOperationException("legacy exterior fixture recipe differs from its variant: " + recipe);
        var expectedReferences = new List<string>();
        if (expectedRecipe is not null) expectedReferences.Add(expectedRecipe + ": footprint 'world-cells'");
        // An upper LegacyExteriorV1 tag is also a role mismatch, so this variant pins exactly both findings.
        if (variant == "upper-tag") expectedReferences.Add("bound surface role mismatch 'world-cells'");
        IReadOnlyList<string> references = MapTopologyReferenceValidator.Validate(set.Refs, set.Patches.Values.ToArray());
        if (!references.Order(StringComparer.Ordinal).SequenceEqual(expectedReferences.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("legacy exterior fixture reference validation differs from its variant: " + string.Join(", ", references));
    }
}
