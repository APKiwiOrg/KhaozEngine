using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Asset render bounds before units or instance transforms, with explicit caller motion padding in metres.
/// Zero padding is valid when supplied. Styling, motion profiles and shadow flags remain caller policy.</summary>
public sealed record MapFoliageModelExtent(string AssetId, MapLocalBounds BoundsInSourceUnits,
    float SourceUnitsToMetres, float MotionPaddingMetres);

/// <summary>An immutable CPU handoff. Only Complete is usable. Bounds are cosmetic padded mesh bounds, null for an
/// empty complete batch or a refusal. DependencyDigest includes used model extents, units and padding independently
/// of Samples.DependencyDigest. No value changes sample selection, physical bounds, statics or navigation.</summary>
public sealed record MapFoliageRenderBatch(MapFoliageSampleBatch Samples, MapFoliageStatus Status,
    MapBox3? Bounds, string DependencyDigest);

internal readonly record struct MapFoliageInstanceRenderBatch(MapFoliageStatus Status, MapBox3? Bounds, string DependencyDigest);

/// <summary>Builds conservative render bounds from exact CPU matrices without resampling or reconstructing poses.
/// Consumers reject stale work using the dependency digest plus generation, retain one batch across every intersected
/// residency membership and release it only after the last membership leaves. Actual streaming and upload are R8.</summary>
public static class MapFoliageRenderHandoff
{
    internal static MapFoliageInstanceRenderBatch Create(MapFoliageInstanceBatch batch, IReadOnlyList<MapFoliageModelExtent> extents) =>
        Build(batch.Status, batch.Count, batch.DependencyDigest,
            i => new(batch.Columns.ModelId(batch.Columns.ModelIndex(i)), batch.Columns[i].Transform), extents);
    /// <summary>Applies source units once to all eight box corners, transforms by each original sample matrix, then
    /// expands XYZ by that model's explicit motion padding. Missing/duplicate/invalid extents refuse with Invalid and
    /// no usable bounds or digest. Incomplete source status propagates. Unused valid extents do not broaden the digest.</summary>
    public static MapFoliageRenderBatch Create(MapFoliageSampleBatch batch, IReadOnlyList<MapFoliageModelExtent> extents)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var result = Build(batch.Status, batch.Samples.Count, batch.DependencyDigest,
            i => new(batch.Samples[i].AssetId, batch.Samples[i].Transform), extents);
        return new(batch, result.Status, result.Bounds, result.DependencyDigest);
    }

    readonly record struct RenderRow(string AssetId, Matrix4x4 Transform);
    static MapFoliageInstanceRenderBatch Build(MapFoliageStatus status, int count, string dependency,
        Func<int, RenderRow> rowAt, IReadOnlyList<MapFoliageModelExtent> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);
        if (status != MapFoliageStatus.Complete) return Refused(status);
        var models = new Dictionary<string, MapFoliageModelExtent>(StringComparer.Ordinal);
        foreach (MapFoliageModelExtent extent in extents)
        {
            if (extent is null || string.IsNullOrWhiteSpace(extent.AssetId) || !Valid(extent) ||
                !models.TryAdd(extent.AssetId, extent)) return Refused(MapFoliageStatus.Invalid);
        }
        var used = new HashSet<string>(StringComparer.Ordinal);
        MapBox3? bounds = null;
        for (int i = 0; i < count; i++)
        {
            var sample = rowAt(i);
            if (!models.TryGetValue(sample.AssetId, out MapFoliageModelExtent? extent) || !Finite(sample.Transform) ||
                sample.Transform.M14 != 0 || sample.Transform.M24 != 0 || sample.Transform.M34 != 0 ||
                sample.Transform.M44 != 1) return Refused(MapFoliageStatus.Invalid);
            used.Add(sample.AssetId);
            MapBox3? sampleBounds = null;
            for (int corner = 0; corner < 8; corner++)
            {
                MapLocalBounds box = extent.BoundsInSourceUnits;
                var source = new Vector3((corner & 1) == 0 ? box.Min.X : box.Max.X,
                    (corner & 2) == 0 ? box.Min.Y : box.Max.Y, (corner & 4) == 0 ? box.Min.Z : box.Max.Z);
                Vector3 point = Vector3.Transform(source * extent.SourceUnitsToMetres, sample.Transform);
                if (!Finite(point)) return Refused(MapFoliageStatus.Invalid);
                var pointBounds = new MapBox3(point.X, point.Y, point.Z, point.X, point.Y, point.Z);
                sampleBounds = sampleBounds?.Union(pointBounds) ?? pointBounds;
            }
            MapBox3 b = sampleBounds!.Value;
            double p = extent.MotionPaddingMetres;
            var padded = new MapBox3(b.MinX - p, b.MinY - p, b.MinZ - p, b.MaxX + p, b.MaxY + p, b.MaxZ + p);
            bounds = bounds?.Union(padded) ?? padded;
        }
        string digest = MapCanonical.HashHex(w =>
        {
            w.WriteStartArray();
            w.WriteStringValue("kemap/foliage-render/1");
            w.WriteStringValue(dependency);
            foreach (string id in used.OrderBy(id => id, StringComparer.Ordinal))
            {
                MapFoliageModelExtent extent = models[id];
                MapLocalBounds box = extent.BoundsInSourceUnits;
                w.WriteStartArray();
                w.WriteStringValue(id);
                foreach (float value in new[] { box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z,
                    extent.SourceUnitsToMetres, extent.MotionPaddingMetres })
                    w.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        });
        return new(MapFoliageStatus.Complete, bounds, digest);

        MapFoliageInstanceRenderBatch Refused(MapFoliageStatus refused) => new(refused, null, "");
    }

    static bool Valid(MapFoliageModelExtent extent)
    {
        MapLocalBounds b = extent.BoundsInSourceUnits;
        return float.IsFinite(extent.SourceUnitsToMetres) && extent.SourceUnitsToMetres > 0 &&
            float.IsFinite(extent.MotionPaddingMetres) && extent.MotionPaddingMetres >= 0 && Finite(b.Min) && Finite(b.Max) &&
            b.Min.X <= b.Max.X && b.Min.Y <= b.Max.Y && b.Min.Z <= b.Max.Z;
    }

    static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    static bool Finite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
