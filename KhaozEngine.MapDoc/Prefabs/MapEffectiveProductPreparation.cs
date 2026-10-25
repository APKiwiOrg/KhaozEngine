using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Prefabs;

/// <summary>Pre-copy admission and inventory for the effective product's owned tables.</summary>
internal static class MapEffectiveProductPreparation
{
    internal static MapPreparationReservation? SourceOwnership(IMapSurfaceSource source) => source switch
    {
        MapEffectiveSurfaceSource effective => effective.PreparationOwnership,
        MapComposedSurfaceSource composed => composed.PreparationOwnership,
        _ => null,
    };
    internal static MapBuildingIntent CopyIntent(MapBuildingIntent intent, MapEffectiveContext context)
    {
        using var preparation = MapSceneAdmission.Current?.BeginPreparation();
        var copy = intent.Clone();
        MapSceneAdmission.Current?.ReserveBytes(checked(128L * copy.RoofLinks.Count + 16L * (copy.RoofGroups.Count + copy.Roofs.Count)));
        var result = new MapBuildingIntent(copy.PayloadVersion, copy.RoofLinks.OrderBy(r => r.Id, StringComparer.Ordinal).Select(link =>
            link with { AboveY = context.Transform(MapPrefabChildKind.RoofLink, link.Id)?.Height(MapExactValue.FromSingle(link.AboveY)).ToSingle() ?? link.AboveY }).ToArray(),
            copy.RoofGroups.OrderBy(g => g.Id, StringComparer.Ordinal).ToArray(), copy.Lights, copy.Markers)
        { Roofs = copy.Roofs.DistinctBy(r => (r.Kind, r.Id)).OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(), Storeys = copy.Storeys };
        // Nested rows remain shared with the clone. Only replaced shells and rows retire here.
        preparation?.Complete(MapBuildingIntentPreparation.Bytes(result));
        return result;
    }
    internal static void BeforeCopy(IReadOnlyList<MapResolvedPlacement> placements, IReadOnlyList<MapPlacementSupport> supports) =>
        MapSceneAdmission.Current?.ReserveBytes(checked(128L + 16L * (placements.Count + supports.Count)));
    internal static IReadOnlyDictionary<string, MapSupportBinding> CopySupports(IReadOnlyDictionary<string, MapSupportBinding> value, out int capacity)
    {
        MapSceneAdmission.Current?.ReserveBytes(checked(128L + 160L * value.Count));
        var rows = value.ToDictionary(p => p.Key, p => p.Value with { }, StringComparer.Ordinal);
        capacity = rows.EnsureCapacity(0);
        return new ReadOnlyDictionary<string, MapSupportBinding>(rows);
    }
    internal static IReadOnlyDictionary<string, MapExactPoint> CopyPoints(IReadOnlyDictionary<string, MapExactPoint> value, out int capacity)
    {
        MapSceneAdmission.Current?.ReserveBytes(checked(128L + 128L * value.Count));
        var rows = new Dictionary<string, MapExactPoint>(value, StringComparer.Ordinal);
        capacity = rows.EnsureCapacity(0);
        return new ReadOnlyDictionary<string, MapExactPoint>(rows);
    }
    internal static IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> CopyCorners(
        IReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>> value, out int capacity)
    {
        MapSceneAdmission.Current?.ReserveBytes(checked(128L + 80L * value.Count + value.Sum(c => 48L * c.Value.Count)));
        var rows = value.ToDictionary(p => p.Key, p => (IReadOnlyList<MapExactPoint>)Array.AsReadOnly(p.Value.ToArray()), StringComparer.Ordinal);
        capacity = rows.EnsureCapacity(0);
        return new ReadOnlyDictionary<string, IReadOnlyList<MapExactPoint>>(rows);
    }
    internal static long Tables(MapEffectiveResolutionProduct product) => checked(40L * product.PaintRegionCapacity +
        (System.Runtime.CompilerServices.Unsafe.SizeOf<MapExactPoint>() + IntPtr.Size + 24L) * product.LightPointCapacity +
        40L * product.WaterCornerCapacity + product.WaterCorners.Sum(c => 8L + 48L * c.Value.Count) +
        IntPtr.Size * (long)product.Attachments.Count + product.OwnedAttachmentBytes + 40L * product.SupportRequestCapacity + 64L * product.SupportRequests.Count +
        IntPtr.Size * product.Placements.Sum(p => (long)p.Tags.Count) +
        IntPtr.Size * product.EffectiveMarkers.Sum(m => (long)m.Tags.Count));
    internal static void BeforeAttachments(IReadOnlyList<MapPrefabLocalMaterialization> rows) =>
        MapSceneAdmission.Current?.ReserveBytes(checked(rows.Sum(r => 256L + 272L * r.Children.Count)));
    internal static long OwnedAttachments(IReadOnlyList<MapPrefabAttachmentTransfer> rows, IReadOnlyList<MapPrefabAttachmentTransfer> borrowed)
    {
        using var workspace = MapSceneAdmission.Current?.Workspace();
        workspace?.Reserve(checked(64L * borrowed.Count));
        var source = borrowed.ToHashSet(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        return checked(rows.Where(r => !source.Contains(r)).Sum(r => 128L + 136L * r.Children.Count));
    }
}
