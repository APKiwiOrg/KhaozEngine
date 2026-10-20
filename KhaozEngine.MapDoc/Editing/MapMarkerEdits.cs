using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Prefabs;

namespace KhaozEngine.MapDoc.Editing;

public abstract record MapMarkerEdit(string Label);

/// <summary>Sets one standalone marker by its immutable ID, creating it when absent. The marker is given in world
/// terms, the form expansion publishes in the effective building intent. A framed standalone marker stores its
/// frame-local equivalent.</summary>
public sealed record MapSetMarker(MapLocalMarkerDoc Marker) : MapMarkerEdit("Set marker");

/// <summary>Removes one standalone marker by ID.</summary>
public sealed record MapRemoveMarker(string Id) : MapMarkerEdit("Remove marker");

/// <summary>Detached standalone marker preparation against the matching verified closure. The ID is the key. A name is
/// edited like any other member and never renames the ID. Effects come only from the shared marker diff.</summary>
public static class MapMarkerEdits
{
    public static MapPrefabEditResult Prepare(MapDocument document, MapAssetClosure assets, MapMarkerEdit edit)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(edit);
        MapDocumentEditFinalizer.Preflight(document, assets);
        RequireResolver(document);
        string id = edit switch
        {
            MapSetMarker set => (set.Marker ?? throw new MapDocumentException("Marker set requires a marker.")).Id,
            MapRemoveMarker remove => remove.Id,
            _ => throw new MapDocumentException("Unknown marker edit."),
        };
        if (string.IsNullOrWhiteSpace(id)) throw new MapDocumentException("Marker edits require a nonempty marker ID.");
        if (edit is MapSetMarker { Marker: var input }) MapMarkerValidation.Require(input);

        var before = MapPrefabEditPreparation.Begin(document, assets);
        var after = MapPrefabEditPreparation.Copy(document);
        var intent = after.BuildingIntent ?? new(1, [], [], [], []);
        var markers = intent.Markers.ToList();
        int index = markers.FindIndex(m => string.Equals(m.Id, id, StringComparison.Ordinal));
        RequireOwnership(document, before, id, index >= 0);
        bool transforms = false, transfers = false;
        switch (edit)
        {
            case MapSetMarker set:
                var frame = (document.NativeResourceTransforms ?? []).SingleOrDefault(t => t.Kind == MapPrefabChildKind.Marker && t.OwnerId == id);
                MapLocalMarkerDoc value = index >= 0 && frame is not null
                    ? Localize(set.Marker, markers[index], before.BuildingIntent.Markers.Single(m => m.Id == id), frame.Transform)
                    : MapPrefabJson.Copy(set.Marker);
                MapMarkerValidation.Require(value);
                if (index >= 0) markers[index] = value;
                else markers.Add(value);
                break;
            case MapRemoveMarker:
                if (index < 0) throw new MapDocumentException($"No standalone marker '{id}'.");
                markers.RemoveAt(index);
                // Only the marker's own frame leaves. Its transfer is retired, never deleted, so the reservation stays.
                if (after.NativeResourceTransforms?.Any(t => Own(t.Kind, t.OwnerId, id)) == true)
                {
                    after.NativeResourceTransforms = after.NativeResourceTransforms.Where(t => !Own(t.Kind, t.OwnerId, id)).ToArray();
                    transforms = true;
                }
                if (after.Prefabs is { } prefabs && prefabs.Transfers.Any(t => !t.Retired && Own(t.Binding.Child.Kind, t.Binding.WorldId, id)))
                {
                    after.Prefabs = new(prefabs.PayloadVersion, prefabs.ExpansionVersion, prefabs.Instances,
                        prefabs.Transfers.Select(t => !t.Retired && Own(t.Binding.Child.Kind, t.Binding.WorldId, id) ? t with { Retired = true } : t).ToArray())
                    { Attachments = prefabs.Attachments };
                    transfers = true;
                }
                break;
        }
        // Every other building-intent member is kept as authored.
        after.BuildingIntent = new(intent.PayloadVersion, intent.RoofLinks, intent.RoofGroups, intent.Lights, markers)
        { Roofs = intent.Roofs, Storeys = intent.Storeys };
        // IDs, names, payloads and bindings are validated, and every support-bound height resolves, before the result exists.
        MapBoundDocumentValidation.Validate(after, assets);
        var resources = MapPrefabResolver.Expand(after, assets);
        var effects = MapMarkerEffects.Describe(MapEffectiveAnnotations.Markers(before), MapEffectiveAnnotations.Markers(resources));
        var writes = new MapNativeWriteSet([], [], [], [], [], [], false)
        {
            BuildingIntent = true,
            NativeResourceTransforms = transforms,
            Prefabs = transfers,
        };
        return MapDocumentEditFinalizer.Complete(document, new(after, writes, effects) { Expansions = new(before, resources) }, assets, assets);
    }

    /// <summary>Resolver (1, 1) documents carry the legacy analytic support contract. Marker heights need authored
    /// support, so resolver adoption is the prerequisite.</summary>
    internal static void RequireResolver(MapDocument document)
    {
        if (document.ResolverIdentity is null) throw new MapDocumentException("Marker editing requires a native document.");
        if (document.ResolverIdentity.ResolverVersion != 2)
            throw new MapDocumentException($"Marker editing requires resolver identity (1, 2), but this document has resolver ({document.ResolverIdentity.PayloadVersion}, " +
                $"{document.ResolverIdentity.ResolverVersion}). Complete resolver adoption first with MapResolverAdoption.ConvertToAuthoredSupport, " +
                "the prerequisite for marker edits.");
    }

    // A live prefab child belongs to its instance. Any marker reservation, live or dormant, belongs to its prefab unless
    // it is the active transfer of the standalone marker being edited.
    static void RequireOwnership(MapDocument document, MapEffectiveResources before, string id, bool standalone)
    {
        if (!standalone && MapEffectiveAnnotations.Markers(before).FirstOrDefault(m => m.Id == id) is { InstanceId: { } instance })
            throw new MapDocumentException($"Marker '{id}' belongs to live prefab instance '{instance}'. Change it with prefab_override, " +
                "using a marker replace or remove override on that instance.");
        var set = document.Prefabs;
        if (set is null) return;
        bool active = set.Transfers.Any(t => !t.Retired && Own(t.Binding.Child.Kind, t.Binding.WorldId, id));
        bool reserved = set.Transfers.Any(t => Own(t.Binding.Child.Kind, t.Binding.WorldId, id)) ||
            set.Instances.Any(i => i.Bindings.Entries.Any(b => Own(b.Child.Kind, b.WorldId, id)));
        if (reserved && !(standalone && active))
            throw new MapDocumentException($"Marker ID '{id}' is a retained prefab reservation and cannot be reused. Choose another marker ID.");
    }

    static bool Own(MapPrefabChildKind kind, string ownerId, string id) =>
        kind == MapPrefabChildKind.Marker && string.Equals(ownerId, id, StringComparison.Ordinal);

    /// <summary>The frame-local form of a world-terms marker under the marker's precise frame, which is kept verbatim.
    /// A component whose world value equals the current effective entry keeps its stored local bits, so metadata edits
    /// never round the point, yaw, height or binding. Changed components invert through the same binary64 frame.</summary>
    static MapLocalMarkerDoc Localize(MapLocalMarkerDoc input, MapLocalMarkerDoc stored, MapLocalMarkerDoc world, MapEffectiveTransform frame)
    {
        var x = stored.Position.X;
        var z = stored.Position.Z;
        if (!Same(input.Position.X, world.Position.X) || !Same(input.Position.Z, world.Position.Z))
        {
            var local = MapPrefabEditPreparation.Relative(frame, frame with { X = input.Position.X, Z = input.Position.Z });
            x = (float)local.X;
            z = (float)local.Z;
        }
        float yaw = Same(input.YawRadians, world.YawRadians) ? stored.YawRadians : (float)(input.YawRadians - frame.YawRadians);
        float y = 0f;
        MapSupportBinding? binding = null;
        if (input.HeightMode == MapMarkerHeightMode.Support)
            binding = LocalBinding(input.SupportBinding!, stored.SupportBinding, world.SupportBinding, frame);
        else
            y = world.HeightMode != MapMarkerHeightMode.Support && Same(input.Position.Y, world.Position.Y)
                ? stored.Position.Y : (float)((input.Position.Y - frame.Y) / frame.Scale);
        return MapPrefabJson.Copy(input with { Position = new(x, y, z), YawRadians = yaw, SupportBinding = binding });
    }

    // Owner IDs are world IDs in both forms. Reference height and searches invert through the frame unless unchanged.
    static MapSupportBinding LocalBinding(MapSupportBinding input, MapSupportBinding? stored, MapSupportBinding? world, MapEffectiveTransform frame)
    {
        var local = MapPrefabResolver.LocalRequest(input, frame);
        if (stored is null || world is null || stored.Kind != input.Kind || world.Kind != input.Kind) return local;
        return local with
        {
            ReferenceY = Same(input.ReferenceY, world.ReferenceY) ? stored.ReferenceY : local.ReferenceY,
            SearchBelow = Same(input.SearchBelow, world.SearchBelow) ? stored.SearchBelow : local.SearchBelow,
            SearchAbove = Same(input.SearchAbove, world.SearchAbove) ? stored.SearchAbove : local.SearchAbove,
        };
    }

    static bool Same(float a, float b) => BitConverter.SingleToUInt32Bits(a) == BitConverter.SingleToUInt32Bits(b);
    static bool Same(float? a, float? b) => a is { } x ? b is { } y && Same(x, y) : b is null;
}
