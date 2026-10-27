using KhaozEngine.Diagnostics;
using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.MapDoc.Surfaces;
using Bounds = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.Bounds;
using SpatialIndex = KhaozEngine.MapDoc.Foliage.MapFoliageGenerationContext.SpatialIndex;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Prepares scoped canonical-owner clearing. Rules are asset/tag metadata and never modify assets, collision,
/// roof visibility or interaction envelopes. Solid/roof footprints test support-cell centres, doors test actual points.</summary>
public static class MapFoliageClearance
{
    public static MapPreparedFoliageClearance Prepare(MapFoliageGenerationContext context, MapFoliageLayer layer,
        MapFoliageChunkKey chunk, MapFoliageLimits limits)
 => Prepare((IMapFoliageInputs)context, layer, chunk, limits);

    internal static MapPreparedFoliageClearance Prepare(IMapFoliageInputs context, MapFoliageLayer layer,
        MapFoliageChunkKey chunk, MapFoliageLimits limits)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(layer);
        ArgumentNullException.ThrowIfNull(limits);
        var support = MapFoliageSupport.Prepare(context, layer, chunk, limits);
        MapFoliageWork work = support.Work;
        var parts = new List<string>(support.DependencyParts);
        var entries = new List<MapPreparedFoliageClearance.Entry>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        if (support.Status != MapFoliageStatus.Complete) return Result(support.Status, support.Detail);
        try
        {
            // Owners are gathered over every point evaluation tests against them. A door tests the sample itself, which lies
            // in the chunk bounds, and its proxy already carries the door radius. Solid and roof footprints and every mask
            // target test support-cell centres: the sample's own and, with edge fade, a neighbour centre's own, all inside
            // the read reach. No other term is needed.
            double reach = MapFoliageSupport.ReadReach(layer, support.Grids.Select(g => (g.Surface, g.Kernel?.Transform)));
            MapFoliageRect b = support.Bounds;
            var rect = new MapFoliageRect(MapFoliageSupport.Outward(b.MinX - reach, lower: true), MapFoliageSupport.Outward(b.MinZ - reach, lower: true),
                MapFoliageSupport.Outward(b.MaxX + reach, lower: false), MapFoliageSupport.Outward(b.MaxZ + reach, lower: false));
            var rules = layer.ClearanceRules.GroupBy(r => r.AssetId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id, StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            var seats = new Dictionary<MapPrefabReference, string>();
            var consulted = new HashSet<string>(StringComparer.Ordinal);
            var placements = context.PlacementsIn(rect, out long inspected);
            if (DemandDiagnostics.Check(context, "MapFoliageClearance.MaxRuleCandidates.1", "MaxRuleCandidates", 0, inspected, limits.MaxRuleCandidates, inspected > limits.MaxRuleCandidates, CapacityDemandLifetime.Cumulative))
            {
                work = work with { RuleCandidates = inspected };
                throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, work, "clearance owner candidates");
            }
            foreach (var placement in placements)
                if (rules.TryGetValue(placement.AssetId, out var matched))
                    foreach (var rule in matched)
                    {
                        work = work with { RuleCandidates = checked(work.RuleCandidates + 1) };
                        if (DemandDiagnostics.Check(context, "MapFoliageClearance.MaxRuleCandidates.2", "MaxRuleCandidates", 0, work.RuleCandidates, limits.MaxRuleCandidates, work.RuleCandidates > limits.MaxRuleCandidates, CapacityDemandLifetime.Cumulative)) throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, work, "clearance rules");
                        if (!Enabled(layer, rule.Kind) || rule.PlacementTag is { } tag && !placement.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))) continue;
                        var owner = MapFoliageOwners.World(MapPrefabChildKind.Placement, placement.PlacementId);
                        var pose = context.Pose(owner);
                        limits.ChargePreparation?.Invoke(4096);
                        entries.Add(new(Seat(owner, pose), pose, rule.Kind, rule.Footprint, null, layer.DoorClearance));
                        Part("rule|" + rule.Id + "|" + rule.AssetId + "|" + rule.PlacementTag + "|" + rule.Kind + "|" + Describe(rule.Footprint));
                        DescribeOwner(owner, pose);
                    }
            var masks = context.ClearanceOwners.Masks(layer.Id, rect, out long maskCandidates);
            work = work with { MaskCandidates = maskCandidates };
            if (DemandDiagnostics.Check(context, "MapFoliageClearance.MaxMaskCandidates.1", "MaxMaskCandidates", 0, maskCandidates, limits.MaxMaskCandidates, maskCandidates > limits.MaxMaskCandidates, CapacityDemandLifetime.Cumulative)) throw new MapFoliageRefusal(MapFoliageStatus.CapacityExceeded, work, "clearance masks");
            foreach (var row in masks)
            {
                var mask = row.Mask;
                if (!Enabled(layer, mask.Kind)) continue;
                limits.ChargePreparation?.Invoke(4096);
                entries.Add(new(Seat(mask.Owner, row.Pose), row.Pose, mask.Kind, mask.Footprint, mask.Support, layer.DoorClearance));
                Part("mask|" + mask.Id + "|" + mask.Owner.Kind + "|" + mask.Owner.Id + "|" + mask.Kind + "|" + Describe(mask.Footprint) + "|" +
                    MapCanonical.HashHex(w => System.Text.Json.JsonSerializer.Serialize(w, mask.Support)));
                DescribeOwner(mask.Owner, row.Pose);
            }
            if (entries.Count != 0)
            {
                var binding = layer.Support;
                MapExactValue low = default, high = default;
                if (binding.Kind == MapSupportBindingKind.Space)
                {
                    var y = MapExactValue.FromSingle(binding.ReferenceY!.Value);
                    low = y.Subtract(MapExactValue.FromSingle(binding.SearchBelow!.Value));
                    high = y.Add(MapExactValue.FromSingle(binding.SearchAbove!.Value));
                }
                // Acquired floors the kernel cannot select inside the binding's search band cannot supply the sample's
                // ordinal. The test is conservative: it only narrows dependencies, and actual selection and membership
                // stay in the shared support kernel.
                foreach (var grid in support.Grids)
                    if (grid.Cells.Count != 0 && (binding.Kind == MapSupportBindingKind.Surface || grid.MeetsBand(low, high)))
                        consulted.Add(grid.Surface.Id);
            }
            foreach (string id in consulted.Order(StringComparer.Ordinal))
            {
                int? ordinal = context.ClearanceOwners.Ordinal(layer.Id, id);
                Part("ordinal|" + id + "|" + (ordinal is null ? "absent" : ordinal.Value.ToString(CultureInfo.InvariantCulture)));
                if (ordinal is { } value) ordinals.Add(id, value);
            }
            Part(string.Create(CultureInfo.InvariantCulture, $"clearance|{layer.ExcludeIndoors}|{layer.ExcludeSolidObjects}|{BitConverter.SingleToInt32Bits(layer.DoorClearance)}|{BitConverter.SingleToInt32Bits(layer.EdgeFade)}"));
            return Result(MapFoliageStatus.Complete, null);

            string Seat(MapPrefabReference owner, MapEffectiveTransform pose)
            {
                if (seats.TryGetValue(owner, out string? seat)) return seat;
                seat = context.ClearanceOwners.Seated(layer, owner, pose, limits, ref work, parts);
                seats.Add(owner, seat);
                consulted.Add(seat);
                return seat;
            }
            void DescribeOwner(MapPrefabReference owner, MapEffectiveTransform pose) => Part(string.Create(CultureInfo.InvariantCulture,
                $"clearance-owner|{owner.Kind}|{owner.Id}|{BitConverter.DoubleToInt64Bits(pose.X)}|{BitConverter.DoubleToInt64Bits(pose.Y)}|{BitConverter.DoubleToInt64Bits(pose.Z)}|{BitConverter.DoubleToInt64Bits(pose.YawRadians)}|{BitConverter.DoubleToInt64Bits(pose.Scale)}"));
        }
        catch (MapFoliageRefusal refusal) { return Result(refusal.Status, refusal.Message); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException) { return Result(MapFoliageStatus.NotRepresentable, error.Message); }
        catch (MapDocumentException error) { return Result(MapCommonRefinement.NotRepresentable(error) ? MapFoliageStatus.NotRepresentable : MapFoliageStatus.Unavailable, error.Message); }

        void Part(string value)
        {
            limits.ChargePreparation?.Invoke(checked(128 + 2L * System.Text.Encoding.UTF8.GetByteCount(value)));
            parts.Add(value);
        }

        MapPreparedFoliageClearance Result(MapFoliageStatus status, string? detail) => new(layer, support, status, detail, work,
            status == MapFoliageStatus.Complete ? parts.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : [],
            status == MapFoliageStatus.Complete ? entries.ToArray() : [], ordinals);
    }

    internal static bool Enabled(MapFoliageLayer layer, MapFoliageClearanceKind kind) => kind switch
    {
        MapFoliageClearanceKind.Solid => layer.ExcludeSolidObjects,
        MapFoliageClearanceKind.RoofAbove => layer.ExcludeIndoors,
        MapFoliageClearanceKind.Door => layer.DoorClearance > 0,
        _ => false,
    };

    static string Describe(MapFoliageRect rect) => string.Create(CultureInfo.InvariantCulture,
        $"{BitConverter.SingleToInt32Bits(rect.MinX)}|{BitConverter.SingleToInt32Bits(rect.MinZ)}|{BitConverter.SingleToInt32Bits(rect.MaxX)}|{BitConverter.SingleToInt32Bits(rect.MaxZ)}");
}

/// <summary>Immutable prepared rules, exception masks and room membership over one support chunk and its cardinal halo.
/// Evaluation never expands resources, changes a payload or acquires/compiles geometry.</summary>
public sealed class MapPreparedFoliageClearance
{
    readonly MapFoliageLayer _layer;
    readonly IReadOnlyDictionary<string, int> _ordinals;
    readonly MapPreparedFoliageSupport _support;
    readonly Entry[] _entries;
    readonly SpatialIndex _index;

    internal MapPreparedFoliageClearance(MapFoliageLayer layer, MapPreparedFoliageSupport support, MapFoliageStatus status,
        string? detail, MapFoliageWork work, string[] parts, Entry[] entries, IReadOnlyDictionary<string, int> ordinals)
    {
        _layer = layer; _support = support; _entries = entries;
        _ordinals = ordinals;
        _index = new(entries.Select(e => e.Bounds).ToArray());
        Status = status; Detail = detail; Work = work; Bounds = support.Bounds;
        DependencyParts = Array.AsReadOnly(parts);
    }

    internal MapPreparedFoliageSupport Support => _support;
    public MapFoliageStatus Status { get; }
    public string? Detail { get; }
    public MapFoliageWork Work { get; }
    public IReadOnlyList<string> DependencyParts { get; }
    public MapFoliageRect Bounds { get; }

    public MapFoliageSurfacePoint Evaluate(MapFoliageSurfacePoint point, float worldX, float worldZ)
    {
        ArgumentNullException.ThrowIfNull(point);
        if (!float.IsFinite(worldX) || !float.IsFinite(worldZ)) throw new ArgumentException("clearance coordinates must be finite");
        if (Status != MapFoliageStatus.Complete) return Refusal(Status);
        if (point.Status != MapFoliageStatus.Complete) return Refusal(point.Status);
        if (point.Reason != MapFoliageReason.Accepted) return point;
        if (worldX < Bounds.MinX || worldX > Bounds.MaxX || worldZ < Bounds.MinZ || worldZ > Bounds.MaxZ)
            throw new ArgumentOutOfRangeException(nameof(worldX), "point lies outside prepared clearance");
        try
        {
            var reason = TileReason(point, worldX, worldZ);
            if (reason != MapFoliageReason.Accepted) return Reject(reason);
            foreach (int index in _index.Query(new(worldX, worldZ, worldX, worldZ), out _))
            {
                Entry entry = _entries[index];
                if (entry.Kind == MapFoliageClearanceKind.Door && Applies(entry, point, worldX, worldZ) &&
                    Vector2.Distance(new(worldX, worldZ), entry.Centre) < entry.DoorRadius)
                    return Reject(MapFoliageReason.Door);
            }
            float density = point.Density;
            // DoorFactor is applied first. Surviving doors multiply by one, then the four cardinal rim factors apply.
            float factor = Edge(point, worldX, worldZ);
            density *= factor;
            return point with { Reason = factor <= 0 ? MapFoliageReason.EdgeZero : MapFoliageReason.Accepted, Density = Math.Clamp(density, 0f, 1f) };
        }
        catch (MapFoliageRefusal refusal) { return Refusal(refusal.Status); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { return Refusal(MapFoliageStatus.NotRepresentable); }
        MapFoliageSurfacePoint Reject(MapFoliageReason reason) => point with { Reason = reason, Density = 0 };
    }

    static MapFoliageSurfacePoint Refusal(MapFoliageStatus status) => new(status, MapFoliageReason.Refused, null, null, null, 0, Vector3.UnitY, 0, 0);

    MapFoliageReason TileReason(MapFoliageSurfacePoint point, float x, float z)
    {
        if (point.Status != MapFoliageStatus.Complete) throw new MapFoliageRefusal(point.Status, Work, "cardinal support refused");
        if (point.Reason != MapFoliageReason.Accepted) return point.Reason;
        if (_layer.ExcludeIndoors && _support.Indoor(point.OccupiedSpaceId)) return MapFoliageReason.Indoor;
        if (Grid(point) is not { } grid) return MapFoliageReason.OutsideDomain;
        var (cx, cz, _, _) = grid.Locate(x, z);
        Vector2 centre = Centre(grid, cx, cz);
        MapFoliageReason reason = MapFoliageReason.Accepted;
        foreach (int i in _index.Query(new(centre.X, centre.Y, centre.X, centre.Y), out _))
        {
            Entry entry = _entries[i];
            if (entry.Kind == MapFoliageClearanceKind.Door || !Applies(entry, point, centre.X, centre.Y)) continue;
            if (!entry.Contains(centre.X, centre.Y)) continue;
            if (entry.Kind == MapFoliageClearanceKind.Solid) return MapFoliageReason.Solid;
            reason = MapFoliageReason.RoofAbove;
        }
        return reason;
    }

    bool Applies(Entry entry, MapFoliageSurfacePoint point, float x, float z)
    {
        if (point.SupportOwnerId is not { } surface || !MapFoliageApplicability.Applies(_ordinals, entry.SeatedSurface, surface, entry.Kind)) return false;
        if (entry.Target is null) return true;
        MapSupportResult target = _support.Target(entry.Target, x, z);
        if (target.Status == MapSupportStatus.NoSupport) return false;
        string? canonical = MapFoliageSupportIdentity.From(target);
        if (canonical is null) throw new MapFoliageRefusal(MapFoliageSupport.Map(target.Status), Work, "mask target refused");
        return canonical == surface;
    }

    float Edge(MapFoliageSurfacePoint point, float x, float z)
    {
        if (_layer.EdgeFade <= 0 || Grid(point) is not { } grid) return 1;
        var (cx, cz, fx, fz) = grid.Locate(x, z);
        float cell = grid.Surface.Frame.CellUnitMetres.Exact().ToSingle() * (float)(grid.Kernel?.Transform.Scale ?? 1);
        float factor = 1f;
        Neighbour(cx - 1, cz, fx * cell);
        Neighbour(cx + 1, cz, (1f - fx) * cell);
        Neighbour(cx, cz - 1, fz * cell);
        Neighbour(cx, cz + 1, (1f - fz) * cell);
        return Math.Clamp(factor, 0f, 1f);

        void Neighbour(long nx, long nz, float distance)
        {
            var centre = Centre(grid, nx, nz);
            MapFoliageSurfacePoint neighbour = _support.Eligibility(centre.X, centre.Y);
            if (TileReason(neighbour, centre.X, centre.Y) != MapFoliageReason.Accepted)
                factor = MathF.Min(factor, distance / _layer.EdgeFade);
        }
    }

    MapFoliageSupportGrid? Grid(MapFoliageSurfacePoint point) => _support.Grids.FirstOrDefault(g => g.Surface.Id == point.Face?.Patch?.SurfaceId);

    static Vector2 Centre(MapFoliageSupportGrid grid, long x, long z)
    {
        var unit = grid.Surface.Frame.CellUnitMetres.Exact();
        var px = new MapExactValue(checked(x * 2 + 1), 2).Multiply(unit);
        var pz = new MapExactValue(checked(z * 2 + 1), 2).Multiply(unit);
        if (grid.Surface.Frame.RowDirection == MapRowDirection.NegativeZ) pz = default(MapExactValue).Subtract(pz);
        var point = new MapExactPoint(px, default, pz);
        if (grid.Kernel is { } kernel) point = kernel.Point(point);
        return new(point.X.ToSingle(), point.Z.ToSingle());
    }

    internal sealed class Entry
    {
        readonly MapExactXz[] _corners;
        internal Entry(string seatedSurface, MapEffectiveTransform pose, MapFoliageClearanceKind kind,
            MapFoliageRect footprint, MapSupportBinding? target, float door)
        {
            SeatedSurface = seatedSurface; Kind = kind; Target = target;
            var kernel = new MapResourceTransform(pose);
            MapExactPoint Point(float x, float z) => kernel.Point(new(MapExactValue.FromSingle(x), default, MapExactValue.FromSingle(z)));
            _corners = new[] { Point(footprint.MinX, footprint.MinZ), Point(footprint.MaxX, footprint.MinZ),
                Point(footprint.MaxX, footprint.MaxZ), Point(footprint.MinX, footprint.MaxZ) }.Select(p => new MapExactXz(p.X, p.Z)).ToArray();
            MapExactPoint centre = Point((footprint.MinX + footprint.MaxX) * 0.5f, (footprint.MinZ + footprint.MaxZ) * 0.5f);
            Centre = new(centre.X.ToSingle(), centre.Z.ToSingle());
            DoorRadius = (float)(door * pose.Scale);
            Bounds = MapFoliageGenerationContext.Proxy(pose, footprint, kind == MapFoliageClearanceKind.Door ? door : null);
        }
        internal string SeatedSurface { get; }
        internal MapFoliageClearanceKind Kind { get; }
        internal MapSupportBinding? Target { get; }
        internal Vector2 Centre { get; }
        internal float DoorRadius { get; }
        internal Bounds Bounds { get; }
        internal bool Contains(float x, float z)
        {
            var p = new MapExactXz(MapExactValue.FromSingle(x), MapExactValue.FromSingle(z));
            for (int i = 0; i < _corners.Length; i++)
            {
                var a = _corners[i]; var b = _corners[(i + 1) % _corners.Length];
                var cross = b.X.Subtract(a.X).Multiply(p.Z.Subtract(a.Z)).Subtract(b.Z.Subtract(a.Z).Multiply(p.X.Subtract(a.X)));
                if (cross.CompareTo(default) < 0) return false;
            }
            return true;
        }
    }
}
