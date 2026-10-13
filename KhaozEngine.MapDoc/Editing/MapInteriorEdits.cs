using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

public abstract record MapInteriorEdit(string Label);
public sealed record MapSetInterior(MapPrismDomain Domain) : MapInteriorEdit("Set interior");
public sealed record MapRemoveInterior(string SpaceId) : MapInteriorEdit("Remove interior");
public sealed record MapSetRoofLink(MapRoofLinkDoc Link) : MapInteriorEdit("Set roof link");
public sealed record MapSetRoofGroup(MapRoofGroupDoc Group) : MapInteriorEdit("Set roof group");

/// <summary>Pure standalone interior and roof intent preparation against the matching captured closure.</summary>
public static class MapInteriorEdits
{
    public static MapPrefabEditResult Prepare(MapDocument document, MapAssetClosure assets, MapInteriorEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var before = MapPrefabEditPreparation.Begin(document, assets);
        var after = MapPrefabEditPreparation.Copy(document);
        var affected = new List<MapPrefabBinding>();
        void Record(MapTopologyRecord record) => affected.Add(new(new(MapPrefabChildKind.Record, record.Id), record.Id, null, false));
        switch (edit)
        {
            case MapSetInterior set:
                ArgumentNullException.ThrowIfNull(set.Domain);
                if (set.Domain.InstanceId != "") throw new MapDocumentException("Standalone interior edits require standalone ownership.");
                if (!after.Surfaces.TryGetRecord(set.Domain.Space, out var space) || space is not MapSpaceDoc)
                    throw new MapDocumentException("Interior names an undeclared standalone room.");
                var old = after.Surfaces.Patches.Values.SelectMany(p => p.Records).OfType<MapPrismDomain>()
                    .SingleOrDefault(p => p.Space.Id == set.Domain.Space.Id);
                if (old is not null && old.Id != set.Domain.Id) throw new MapDocumentException("Interior replacement must retain its record ID.");
                foreach (var patch in after.Surfaces.Patches.Values) patch.Records.RemoveAll(r => r.Id == set.Domain.Id);
                after.Surfaces.Patches[set.Domain.Space.Anchor].Records.Add(set.Domain);
                Record(set.Domain); Record(space);
                break;
            case MapRemoveInterior remove:
                var records = after.Surfaces.AllRecords().Where(r => r is MapSpaceDoc s && s.Id == remove.SpaceId ||
                    r is MapPrismDomain p && p.Space.Id == remove.SpaceId).ToArray();
                if (!records.OfType<MapPrismDomain>().Any()) throw new MapDocumentException("No standalone interior for that space.");
                foreach (var record in records) Record(record);
                var ids = records.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var patch in after.Surfaces.Patches.Values) patch.Records.RemoveAll(r => ids.Contains(r.Id));
                after.NativeResourceTransforms = after.NativeResourceTransforms?.Where(t => t.Kind != MapPrefabChildKind.Record || !ids.Contains(t.OwnerId)).ToArray();
                if (after.Prefabs is { } prefabs) after.Prefabs = new(prefabs.PayloadVersion, prefabs.ExpansionVersion, prefabs.Instances,
                    prefabs.Transfers.Select(t => t.Binding.Child.Kind == MapPrefabChildKind.Record && ids.Contains(t.Binding.WorldId) ? t with { Retired = true } : t).ToArray())
                { Attachments = prefabs.Attachments };
                break;
            case MapSetRoofLink set:
                ArgumentNullException.ThrowIfNull(set.Link);
                var links = after.BuildingIntent ?? new(1, [], [], [], []);
                after.BuildingIntent = new(1, links.RoofLinks.Where(l => l.Id != set.Link.Id).Append(set.Link).ToArray(),
                    links.RoofGroups, links.Lights, links.Markers)
                { Roofs = links.Roofs, Storeys = links.Storeys };
                affected.Add(new(new(MapPrefabChildKind.RoofLink, set.Link.Id), set.Link.Id, null, false));
                break;
            case MapSetRoofGroup set:
                ArgumentNullException.ThrowIfNull(set.Group);
                var groups = after.BuildingIntent ?? new(1, [], [], [], []);
                after.BuildingIntent = new(1, groups.RoofLinks, groups.RoofGroups.Where(g => g.Id != set.Group.Id).Append(set.Group).ToArray(),
                    groups.Lights, groups.Markers)
                { Roofs = groups.Roofs, Storeys = groups.Storeys };
                affected.Add(new(new(MapPrefabChildKind.RoofGroup, set.Group.Id), set.Group.Id, null, false));
                break;
            default: throw new MapDocumentException("Unknown interior edit.");
        }
        return MapPrefabEditPreparation.Finish(after, assets, before, [], affected, interiors: true);
    }
}
