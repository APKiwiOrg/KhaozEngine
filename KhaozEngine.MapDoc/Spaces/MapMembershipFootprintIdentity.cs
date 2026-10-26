using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Full effective geometry identity. Scope and bound policy belong to the private view.</summary>
internal sealed class MapMembershipFootprintIdentity
{
    readonly object _source;
    readonly string _closure, _generation, _record, _path;
    readonly string? _recordVersion, _latticeVersion;
    readonly MapPatchKey _lattice;
    readonly MapLatticeFrame _frame;
    readonly MapEffectiveTransform? _transform;
    readonly int[] _slots;
    readonly int _boundary, _bits;
    internal MapMembershipFootprintIdentity(object source, string closure, string generation, string record,
        string? recordVersion, string? latticeVersion, MapPatchKey lattice, MapLatticeFrame frame, string path,
        MapEffectiveTransform? transform, IReadOnlyList<int> slots, int boundary, int bits)
    {
        _source = source; _closure = closure; _generation = generation; _record = record;
        _recordVersion = recordVersion; _latticeVersion = latticeVersion; _lattice = lattice; _frame = frame;
        _path = path; _transform = transform; _slots = slots.ToArray(); _boundary = boundary; _bits = bits;
    }
    bool Matches(object source, string closure, string generation, string record, string? recordVersion,
        string? latticeVersion, MapPatchKey lattice, MapLatticeFrame frame, string path,
        MapEffectiveTransform? transform, IReadOnlyList<int> slots, int boundary, int bits)
    {
        if (!ReferenceEquals(_source, source) || _closure != closure || _generation != generation || _record != record ||
            _recordVersion != recordVersion || _latticeVersion != latticeVersion || _lattice != lattice ||
            _frame != frame || _path != path || _transform != transform || _boundary != boundary || _bits != bits ||
            _slots.Length != slots.Count) return false;
        for (int i = 0; i < _slots.Length; i++) if (_slots[i] != slots[i]) return false;
        return true;
    }
    public override bool Equals(object? obj) => obj is MapMembershipFootprintIdentity other &&
        Matches(other._source, other._closure, other._generation, other._record, other._recordVersion,
            other._latticeVersion, other._lattice, other._frame, other._path, other._transform, other._slots, other._boundary, other._bits);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(_source);
    internal bool Matches(MapScopedScene scene, MapSpaceFootprint footprint)
    {
        var v = scene.SurfaceView;
        return Matches(scene.Source, v.Identity.RootSha256, v.Witness.SnapshotId, footprint.Id,
            RecordVersion(v, footprint.Id), Version(v, footprint.Lattice), footprint.Lattice,
            MapMembershipPreparation.Surface(v, footprint.Lattice.SurfaceId).Frame, footprint.Lattice.SurfaceId,
            Pose(v, footprint.Lattice.SurfaceId), footprint.SlotCells, 1, MapMembershipInteger.CoefficientBits);
    }
    internal static MapMembershipFootprintIdentity Capture(MapScopedScene scene, MapSpaceFootprint footprint)
    {
        var v = scene.SurfaceView;
        return new(scene.Source, v.Identity.RootSha256, v.Witness.SnapshotId, footprint.Id,
            RecordVersion(v, footprint.Id), Version(v, footprint.Lattice), footprint.Lattice,
            MapMembershipPreparation.Surface(v, footprint.Lattice.SurfaceId).Frame, footprint.Lattice.SurfaceId,
            Pose(v, footprint.Lattice.SurfaceId), footprint.SlotCells, 1, MapMembershipInteger.CoefficientBits);
    }
    static string? Version(MapScopedSurfaces view, MapPatchKey patch)
    { foreach (var p in view.Witness.Present) if (p.Key == patch) return p.Value; return null; }
    static string? RecordVersion(MapScopedSurfaces view, string id)
    { foreach (var r in view.Witness.Records) if (r.Id == id) return Version(view, r.Anchor); return null; }
    static MapEffectiveTransform? Pose(MapScopedSurfaces view, string id) => view.EffectiveContext is { } c &&
        c.TryGeometryTransform(MapPrefabChildKind.Surface, id, out var p) ? p : null;
}
