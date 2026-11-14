using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Required reference members the prefab validator dereferences. The JSON codec refuses a missing or null
/// required member before a DTO exists, but state constructed in code reaches validation without that pass, and a
/// nonnullable reference property is not guaranteed nonnull. Each check runs before the first dereference and refuses
/// with <see cref="MapDocumentRefusal.SchemaValueRequired"/>, the member's <c>FieldPath</c> and its owner, so an
/// unexpected null dereference escapes validation as the defect it is instead of reading as authored state.</summary>
internal static class MapPrefabRequiredMembers
{
    /// <summary>Transfers are read by the instance loop and the standalone storey check, so each row and its binding is
    /// checked before either. Paths are rooted at the document because a transfer has no identity without its binding.</summary>
    internal static void Transfers(IReadOnlyList<MapPrefabBindingTransfer> transfers, string documentId)
    {
        for (int index = 0; index < transfers.Count; index++)
        {
            string path = $"$.prefabs.transfers[{index}]";
            MapPrefabBindingTransfer? transfer = transfers[index];
            Require(transfer, path, "transfer", documentId: documentId, index: index);
            Require(transfer.Binding, path + ".binding", "transfer", documentId: documentId, instanceId: transfer.InstanceId, index: index);
        }
    }

    internal static void Instance(MapPrefabInstance instance) =>
        Require(instance.Bindings, "$.bindings", "prefab instance", instanceId: instance.Id);

    internal static void Override(MapPrefabOverride value)
    {
        if (value is MapPrefabReplaceOverride replace) Require(replace.Value, "$.value", "override", child: value.Child);
        else if (value is MapPrefabAddOverride add) Require(add.Value, "$.value", "override", child: value.Child);
    }

    internal static void Child(MapPrefabChild child)
    {
        Require(child.References, "$.references", "prefab child", child: child.Key);
        switch (child)
        {
            case MapPrefabPlacementChild p: Require(p.Value, "$.value", "prefab child", child: child.Key); break;
            case MapPrefabSurfaceChild s:
                Require(s.Surface, "$.surface", "prefab child", child: child.Key);
                Require(s.Patches, "$.patches", "prefab child", child: child.Key);
                break;
            case MapPrefabRecordChild r: Require(r.Value, "$.value", "prefab child", child: child.Key); break;
            case MapPrefabMediumChild m: Require(m.Value, "$.value", "prefab child", child: child.Key); break;
            case MapPrefabWaterChild w:
                Require(w.Value, "$.value", "prefab child", child: child.Key);
                // The closure lookup in RequireLeafResources dereferences the render material.
                Require(w.Value.RenderMaterial, "$.value.renderMaterial", "prefab child", child: child.Key);
                break;
            case MapPrefabLightChild l: Require(l.Value, "$.value", "prefab child", child: child.Key); break;
            case MapPrefabMarkerChild m: Require(m.Value, "$.value", "prefab child", child: child.Key); break;
            case MapPrefabRoofChild r: Require(r.Value, "$.value", "prefab child", child: child.Key); break;
            case MapPrefabRoofGroupChild g: Require(g.Value, "$.value", "prefab child", child: child.Key); break;
        }
        MapPrefabReferenceMembers.Child(child);
    }

    internal static void RoofLink(MapRoofLinkDoc roof)
    {
        Require(roof.Roof, "$.roof", "roof link", entityId: roof.Id);
        Elements(roof.Rooms, "$.rooms", "roof link", roof.Id);
        Elements(roof.Groups, "$.groups", "roof link", roof.Id);
    }

    internal static void RoofGroup(MapRoofGroupDoc group) => Elements(group.Rooms, "$.rooms", "roof group", group.Id);

    static void Elements(IReadOnlyList<MapPrefabReference> values, string path, string subjectKind, string entityId)
    {
        Require(values, path, subjectKind, entityId: entityId);
        for (int index = 0; index < values.Count; index++)
            Require(values[index], $"{path}[{index}]", subjectKind, entityId: entityId, index: index);
    }

    internal static void Require([NotNull] object? value, string path, string subjectKind, string? documentId = null, string? instanceId = null,
        MapPrefabChildKey? child = null, string? entityId = null, int? index = null)
    {
        if (value is null) throw new MapDocumentException(MapDocumentRefusal.SchemaValueRequired, "Required prefab member '" + path + "' must not be null.")
        {
            FieldPath = path,
            SubjectKind = subjectKind,
            DocumentId = documentId,
            InstanceId = instanceId,
            Child = child,
            ChildKind = child?.Kind,
            EntityId = entityId ?? child?.Key,
            Index = index,
        };
    }
}
