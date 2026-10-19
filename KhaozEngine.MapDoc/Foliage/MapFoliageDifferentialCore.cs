using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Value-based reconciliation of at most 4096 stable addresses. Versioned keys, canonical support and occupied
/// space remain compared attributes, never join keys. Generation ownership and dependency digests are not semantics.</summary>
public static class MapFoliageDifferentialCore
{
    public static IReadOnlyList<MapFoliageChange> JoinBlock(IReadOnlyList<MapFoliageDecision> before, IReadOnlyList<MapFoliageDecision> after) =>
        JoinBlock(before, after, null);

    internal static IReadOnlyList<MapFoliageChange> JoinBlock(IReadOnlyList<MapFoliageDecision> before, IReadOnlyList<MapFoliageDecision> after,
        Action? admitChange)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count > 4096 || after.Count > 4096) throw new ArgumentException("comparison blocks hold at most 4096 address pairs");
        var a = Index(before);
        var b = Index(after);
        var addresses = a.Keys.Union(b.Keys).OrderBy(k => k.Z).ThenBy(k => k.X).ThenBy(k => k.LayerId, StringComparer.Ordinal);
        var rows = new List<MapFoliageChange>();
        int count = 0;
        foreach (MapFoliageComparisonAddress address in addresses)
        {
            if (++count > 4096) throw new ArgumentException("comparison blocks hold at most 4096 address pairs");
            a.TryGetValue(address, out MapFoliageDecision? old);
            b.TryGetValue(address, out MapFoliageDecision? next);
            bool had = old?.Sample is not null, has = next?.Sample is not null;
            MapFoliageChangeKind? kind = had != has ? has ? MapFoliageChangeKind.Added : MapFoliageChangeKind.Removed
                : old is not null && next is not null && !Equal(old, next) ? MapFoliageChangeKind.Changed : null;
            if (kind is null) continue;
            admitChange?.Invoke();
            rows.Add(new(address, kind.Value, old, next));
        }
        return rows.AsReadOnly();
    }

    static Dictionary<MapFoliageComparisonAddress, MapFoliageDecision> Index(IReadOnlyList<MapFoliageDecision> rows)
    {
        var result = new Dictionary<MapFoliageComparisonAddress, MapFoliageDecision>();
        foreach (MapFoliageDecision row in rows)
            if (!result.TryAdd(new(row.Address.LayerId, row.Address.X, row.Address.Z), row))
                throw new ArgumentException("duplicate stable comparison address");
        return result;
    }

    static bool Equal(MapFoliageDecision a, MapFoliageDecision b)
    {
        if (a.Address != b.Address || a.OccupiedSpaceId != b.OccupiedSpaceId || a.Reason != b.Reason || a.Distribution.Kind != b.Distribution.Kind ||
            !F(a.Distribution.Scale, b.Distribution.Scale) || !F(a.Distribution.YawRadians, b.Distribution.YawRadians)) return false;
        var x = a.Distribution.Sample;
        var y = b.Distribution.Sample;
        if (x.HasValue != y.HasValue || x is { } p && y is { } q &&
            (!F(p.Height, q.Height) || !V(p.Normal, q.Normal) || !F(Math.Clamp(p.Density, 0f, 1f), Math.Clamp(q.Density, 0f, 1f)))) return false;
        if ((a.Surface is null) != (b.Surface is null) || a.Surface is { } s && b.Surface is { } t &&
            (s.Status != t.Status || s.VisibleMaterial != t.VisibleMaterial || s.Face != t.Face || s.SupportOwnerId != t.SupportOwnerId || s.OccupiedSpaceId != t.OccupiedSpaceId)) return false;
        if (a.Sample is not { } old || b.Sample is not { } next) return a.Sample is null && b.Sample is null;
        return old.Key.Address == next.Key.Address && old.Key.DistributionVersion == next.Key.DistributionVersion &&
            old.Key.Policy.Id == next.Key.Policy.Id && old.Key.Policy.Version == next.Key.Policy.Version &&
            F(old.Key.Policy.NormalBoundaryHeight, next.Key.Policy.NormalBoundaryHeight) && old.OccupiedSpaceId == next.OccupiedSpaceId &&
            old.Face == next.Face && old.AssetId == next.AssetId && old.MaterialResourceIds.SequenceEqual(next.MaterialResourceIds, StringComparer.Ordinal) &&
            old.VisibleMaterial == next.VisibleMaterial && V(old.Position, next.Position) &&
            F(old.Orientation.X, next.Orientation.X) && F(old.Orientation.Y, next.Orientation.Y) && F(old.Orientation.Z, next.Orientation.Z) && F(old.Orientation.W, next.Orientation.W) &&
            F(old.Scale, next.Scale) && F(old.ThinningRank, next.ThinningRank) && Matrix(old.Transform, next.Transform);
    }

    static bool F(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool V(Vector3 a, Vector3 b) => F(a.X, b.X) && F(a.Y, b.Y) && F(a.Z, b.Z);
    static bool Matrix(Matrix4x4 a, Matrix4x4 b) =>
        F(a.M11, b.M11) && F(a.M12, b.M12) && F(a.M13, b.M13) && F(a.M14, b.M14) &&
        F(a.M21, b.M21) && F(a.M22, b.M22) && F(a.M23, b.M23) && F(a.M24, b.M24) &&
        F(a.M31, b.M31) && F(a.M32, b.M32) && F(a.M33, b.M33) && F(a.M34, b.M34) &&
        F(a.M41, b.M41) && F(a.M42, b.M42) && F(a.M43, b.M43) && F(a.M44, b.M44);

    // The attestation hashes the same value channels without list or record identity.
    internal static string Fingerprint(MapFoliageDecision row) => MapCanonical.HashHex(w => Write(w, row));

    static void Write(Utf8JsonWriter w, MapFoliageDecision row)
    {
        w.WriteStartArray();
        w.WriteStringValue(row.Address.LayerId); w.WriteNumberValue(row.Address.X); w.WriteNumberValue(row.Address.Z);
        w.WriteStringValue(row.Address.SupportOwnerId); w.WriteStringValue(row.OccupiedSpaceId);
        w.WriteNumberValue((int)row.Reason); w.WriteNumberValue((int)row.Distribution.Kind); F(row.Distribution.Scale); F(row.Distribution.YawRadians);
        if (row.Distribution.Sample is { } sampled) { F(sampled.Height); V(sampled.Normal); F(Math.Clamp(sampled.Density, 0f, 1f)); }
        if (row.Surface is { } point)
        {
            w.WriteNumberValue((int)point.Status);
            w.WriteStringValue(point.SupportOwnerId); w.WriteStringValue(point.OccupiedSpaceId);
            Face(point.Face); w.WriteNumberValue(point.VisibleMaterial);
        }
        if (row.Sample is { } sample)
        {
            w.WriteNumberValue(sample.Key.DistributionVersion);
            w.WriteStringValue(sample.Key.Policy.Id); w.WriteNumberValue(sample.Key.Policy.Version); F(sample.Key.Policy.NormalBoundaryHeight);
            w.WriteStringValue(sample.Key.Address.SupportOwnerId); w.WriteStringValue(sample.OccupiedSpaceId); Face(sample.Face);
            w.WriteStringValue(sample.AssetId); w.WriteStartArray();
            foreach (string id in sample.MaterialResourceIds) w.WriteStringValue(id);
            w.WriteEndArray(); w.WriteNumberValue(sample.VisibleMaterial); V(sample.Position);
            F(sample.Orientation.X); F(sample.Orientation.Y); F(sample.Orientation.Z); F(sample.Orientation.W);
            F(sample.Scale); F(sample.ThinningRank);
            Matrix4x4 m = sample.Transform;
            F(m.M11); F(m.M12); F(m.M13); F(m.M14); F(m.M21); F(m.M22); F(m.M23); F(m.M24);
            F(m.M31); F(m.M32); F(m.M33); F(m.M34); F(m.M41); F(m.M42); F(m.M43); F(m.M44);
        }
        w.WriteEndArray();
        void F(float v) => w.WriteNumberValue(BitConverter.SingleToInt32Bits(v));
        void V(Vector3 v) { F(v.X); F(v.Y); F(v.Z); }
        void Face(Surfaces.MapFaceKey? face)
        {
            if (face is not { } value) { w.WriteNullValue(); return; }
            w.WriteStringValue(value.OwnerId);
            if (value.Patch is { } p) { w.WriteStringValue(p.SurfaceId); w.WriteNumberValue(p.SlotX); w.WriteNumberValue(p.SlotZ); }
            else w.WriteNullValue();
            w.WriteNumberValue(value.Primitive); w.WriteNumberValue(value.ParentTriangle); w.WriteNumberValue(value.Child); w.WriteNumberValue((int)value.Side);
        }
    }
}
