using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Scene;

/// <summary>Descriptor-derived conservative discovery, independent of collision tags and unresolved support height.</summary>
internal static class MapSceneResourceBounds
{
    internal static MapBox3? Child(MapPrefabChild child, MapEffectiveTransform pose, MapAssetClosure assets, MapSceneShapeReach shapes) => child switch
    {
        MapPrefabPlacementChild placement => Placement(assets.GetAsset(placement.Value.AssetId!), pose, shapes),
        MapPrefabLightChild light => Light(light.Value, pose),
        MapPrefabMarkerChild marker => Point(marker.Value.Position, pose),
        MapPrefabWaterChild water => Domain(water.Value.Domain, pose),
        MapPrefabSurfaceChild surface => Surface(surface, pose),
        MapPrefabRecordChild { Value: MapPrismDomain prism } => Domain(prism.Footprint, pose),
        _ => null,
    };
    static MapBox3 Placement(MapResolvedAsset asset, MapEffectiveTransform pose, MapSceneShapeReach shapes)
    {
        var bounds = Box(asset.RenderBounds, asset.SourceUnitsToMetres, pose);
        if (asset.LodBounds is { } lod) bounds = bounds.Union(Box(lod, asset.SourceUnitsToMetres, pose));
        if (asset.LightBounds is { } light) bounds = bounds.Union(Box(light, asset.SourceUnitsToMetres, pose));
        double radius = shapes.Radius(asset) * asset.SourceUnitsToMetres * pose.Scale;
        if (radius > 0) bounds = bounds.Union(Outward(pose.X - radius, pose.Z - radius, pose.X + radius, pose.Z + radius));
        return bounds;
    }
    static MapBox3 Box(MapLocalBounds bounds, double unit, MapEffectiveTransform pose)
    {
        MapEffectiveTransform.Valid(pose);
        var (sin, cos) = Math.SinCos(pose.YawRadians);
        double minX = double.PositiveInfinity, minZ = double.PositiveInfinity, maxX = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        foreach (double x in new[] { (double)bounds.Min.X, bounds.Max.X })
            foreach (double z in new[] { (double)bounds.Min.Z, bounds.Max.Z })
            {
                double wx = pose.X + (cos * x + sin * z) * unit * pose.Scale;
                double wz = pose.Z + (-sin * x + cos * z) * unit * pose.Scale;
                minX = Math.Min(minX, wx); minZ = Math.Min(minZ, wz); maxX = Math.Max(maxX, wx); maxZ = Math.Max(maxZ, wz);
            }
        return Outward(minX, minZ, maxX, maxZ);
    }
    static MapBox3 Outward(double minX, double minZ, double maxX, double maxZ)
    {
        const double freeze = 1.0 / MapResourceTransform.Quantum;
        if (!double.IsFinite(minX) || !double.IsFinite(minZ) || !double.IsFinite(maxX) || !double.IsFinite(maxZ)) throw new MapExactOverflowException();
        return new(Math.BitDecrement(minX - freeze), double.NegativeInfinity, Math.BitDecrement(minZ - freeze),
            Math.BitIncrement(maxX + freeze), double.PositiveInfinity, Math.BitIncrement(maxZ + freeze));
    }
    internal static MapBox3 Point(Vector3 point, MapEffectiveTransform pose) => Point(point.X, point.Z, pose);
    static MapBox3 Point(double x, double z, MapEffectiveTransform pose)
    {
        var (sin, cos) = Math.SinCos(pose.YawRadians);
        double wx = pose.X + (cos * x + sin * z) * pose.Scale, wz = pose.Z + (-sin * x + cos * z) * pose.Scale;
        return Outward(wx, wz, wx, wz);
    }
    internal static MapBox3 Rectangle(Storage.MapCellRect cells, MapLatticeFrame frame, MapEffectiveTransform pose)
    {
        MapBox3? result = null;
        foreach (long x in new[] { cells.MinX, cells.MaxXExclusive })
            foreach (long z in new[] { cells.MinZ, cells.MaxZExclusive })
            {
                var point = frame.WorldXz(MapLatticeAddress.Corner(x, z));
                var b = Point(point.X.ToDouble(), point.Z.ToDouble(), pose);
                result = result?.Union(b) ?? b;
            }
        return result!.Value;
    }
    internal static MapBox3 Light(MapLocalLightDoc light, MapEffectiveTransform pose)
    {
        var b = Point(light.Position, pose); double radius = light.Radius * pose.Scale;
        return b with { MinX = b.MinX - radius, MaxX = b.MaxX + radius, MinZ = b.MinZ - radius, MaxZ = b.MaxZ + radius };
    }
    static MapBox3? Surface(MapPrefabSurfaceChild surface, MapEffectiveTransform pose)
    {
        MapBox3? result = null;
        foreach (var patch in surface.Patches)
        {
            long x = checked(patch.Key.SlotX * 64 + patch.CellMinX), z = checked(patch.Key.SlotZ * 64 + patch.CellMinZ);
            var bounds = Rectangle(new(x, z, checked(x + patch.Width), checked(z + patch.Depth)), surface.Surface.Frame, pose);
            result = result?.Union(bounds) ?? bounds;
        }
        return result;
    }
    internal static MapBox3 Domain(MapWaterDomainDoc domain, MapEffectiveTransform pose)
    {
        var addresses = domain.Rects.SelectMany(r => new[] { MapLatticeAddress.Corner(r.MinX, r.MinZ), MapLatticeAddress.Corner(r.MaxX, r.MinZ),
            MapLatticeAddress.Corner(r.MaxX, r.MaxZ), MapLatticeAddress.Corner(r.MinX, r.MaxZ) }).Concat(domain.Rings.SelectMany(r => r.Vertices));
        MapBox3? result = null;
        foreach (var address in addresses)
        {
            var point = domain.Frame.WorldXz(address); var bounds = Point(point.X.ToDouble(), point.Z.ToDouble(), pose);
            result = result?.Union(bounds) ?? bounds;
        }
        return result ?? throw new MapDocumentException("Empty scene resource domain.");
    }
    internal static MapBox3 Foliage(MapFoliageLayer layer, MapFoliageChunkKey chunk, MapAssetClosure assets)
    {
        double radius = Math.Abs(layer.RootOffset);
        foreach (var model in layer.Models)
        {
            var asset = assets.GetAsset(model.AssetId);
            foreach (var bounds in new MapLocalBounds?[] { asset.RenderBounds, asset.LodBounds, asset.LightBounds })
                if (bounds is { } b)
                {
                    double x = Math.Max(Math.Abs(b.Min.X), Math.Abs(b.Max.X));
                    double y = Math.Max(Math.Abs(b.Min.Y), Math.Abs(b.Max.Y));
                    double z = Math.Max(Math.Abs(b.Min.Z), Math.Abs(b.Max.Z));
                    // A sphere covers yaw and every support-aligned orientation, including a tilted long blade.
                    radius = Math.Max(radius, Math.Sqrt(x * x + y * y + z * z) * asset.SourceUnitsToMetres * layer.ScaleMax + Math.Abs(layer.RootOffset));
                }
        }
        var rect = MapFoliageSupport.Rect(layer, chunk);
        return Outward(rect.MinX - radius, rect.MinZ - radius, rect.MaxX + radius, rect.MaxZ + radius);
    }

    internal static IEnumerable<MapSceneResourceKey> Dependencies(MapPrefabChild child)
    {
        foreach (var reference in child.References) yield return new(reference.Target.Kind.ToString(), reference.Target.Id);
        switch (child)
        {
            case MapPrefabPlacementChild placement:
                foreach (var key in Binding(placement.Value.SupportBinding)) yield return key;
                break;
            case MapPrefabSurfaceChild surface:
                if (surface.Surface.PaintTargetSurfaceId is { } target) yield return new("Surface", target);
                if (surface.Surface.IndoorSpan?.ParentSpace is { } parent) yield return new("Record", parent.Id);
                break;
            case MapPrefabRecordChild record:
                yield return new("Surface", record.Anchor.SurfaceId);
                foreach (var reference in Storage.MapSurfaceReferences.Records(record.Value)) yield return new("Record", reference.Id);
                break;
            case MapPrefabWaterChild water:
                foreach (var reference in water.Value.Covers) yield return new("Record", reference.Id);
                yield return new("WaterMedium", water.Value.Medium);
                break;
            case MapPrefabLightChild { Value.Owner: { } owner }: yield return new(owner.Kind.ToString(), owner.Id); break;
            case MapPrefabMarkerChild marker:
                foreach (var key in Binding(marker.Value.SupportBinding)) yield return key;
                break;
            case MapPrefabRoofChild roof:
                foreach (var reference in new[] { roof.Value.Roof }.Concat(roof.Value.Rooms).Concat(roof.Value.Groups)) yield return new(reference.Kind.ToString(), reference.Id);
                break;
            case MapPrefabRoofGroupChild group:
                foreach (var reference in group.Value.Rooms) yield return new(reference.Kind.ToString(), reference.Id);
                break;
        }
    }
    internal static IEnumerable<MapSceneResourceKey> Binding(MapSupportBinding? binding)
    {
        if (binding?.SurfaceId is { } surface) yield return new("Surface", surface);
        if (binding?.SpaceId is { } space) yield return new("Record", space);
    }
    internal static long MetadataBytes(MapPrefabChild child)
    {
        long count = Encoding.UTF8.GetByteCount(child.Key.Key);
        foreach (var reference in child.References) count = checked(count + Encoding.UTF8.GetByteCount(reference.Path) + Encoding.UTF8.GetByteCount(reference.Target.Id) + 32);
        if (child is MapPrefabPlacementChild p)
        {
            count = checked(count + Encoding.UTF8.GetByteCount(p.Value.AssetId ?? "") + Encoding.UTF8.GetByteCount(p.Value.Kind) + 48);
            foreach (string tag in p.Value.Tags) count = checked(count + Encoding.UTF8.GetByteCount(tag));
        }
        if (child is MapPrefabMarkerChild m)
        {
            count = checked(count + Encoding.UTF8.GetByteCount(m.Value.Name) + Encoding.UTF8.GetByteCount(m.Value.Role));
            foreach (string tag in m.Value.Tags) count = checked(count + Encoding.UTF8.GetByteCount(tag));
        }
        return count;
    }
    internal static long InstanceBytes(MapPrefabInstance instance)
    {
        long bytes = checked(128L + Encoding.UTF8.GetByteCount(instance.Id) + Encoding.UTF8.GetByteCount(instance.ResourceId) +
            Encoding.UTF8.GetByteCount(instance.ResourceSha256) + instance.Bindings.Entries.Count * 32L + instance.Overrides.Count * 8L);
        foreach (var binding in instance.Bindings.Entries) bytes = checked(bytes + Encoding.UTF8.GetByteCount(binding.Child.Key) + Encoding.UTF8.GetByteCount(binding.WorldId));
        foreach (var value in instance.Overrides)
            for (MapPrefabOverride? item = value; item is not null; item = (item as MapPrefabRemoveOverride)?.Previous)
            {
                bytes = checked(bytes + 64L + Encoding.UTF8.GetByteCount(item.Child.Key));
                var child = item is MapPrefabAddOverride add ? add.Value : (item as MapPrefabReplaceOverride)?.Value;
                if (child is not null)
                {
                    bytes = checked(bytes + MetadataBytes(child));
                    if (child is MapPrefabSurfaceChild surface) foreach (var patch in surface.Patches) bytes = checked(bytes + MapSceneAdmission.PatchBytes(patch));
                }
                if (item is MapPrefabPlacementOverride placement)
                {
                    bytes = checked(bytes + Encoding.UTF8.GetByteCount(placement.AssetId ?? "") + (placement.Tags?.Count ?? 0) * 8L);
                    foreach (string tag in placement.Tags ?? []) bytes = checked(bytes + Encoding.UTF8.GetByteCount(tag));
                }
            }
        return bytes;
    }
    internal static long FoliageMetadataBytes(MapFoliageLayer layer)
    {
        long bytes = checked(Encoding.UTF8.GetByteCount(layer.Id) + layer.Models.Count * 16L + layer.Materials.Count * 16L +
            layer.AllowedVisibleMaterials.Count * 2L + layer.ChunkDigests.Count * 80L + layer.CompatibilityOrdinals.Count * 24L +
            layer.ClearanceRules.Count * 64L + layer.ExceptionMasks.Count * 96L + layer.OwnerSupports.Count * 64L);
        foreach (var model in layer.Models) bytes = checked(bytes + Encoding.UTF8.GetByteCount(model.AssetId));
        foreach (var material in layer.Materials) bytes = checked(bytes + Encoding.UTF8.GetByteCount(material.ResourceId));
        return bytes;
    }

}
