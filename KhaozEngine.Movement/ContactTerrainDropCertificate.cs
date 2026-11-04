using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>One named raw transition's immutable topology. Physical coverage never grants ordinary nodes.</summary>
internal sealed class ContactTerrainDropCertificate
{
    readonly PhysicsNavDescentEvidence _evidence;
    readonly Dictionary<MovementSurfaceFeatureKey, int> _keys = [];
    readonly HashSet<int> _upper, _steep, _landing, _sourceCell, _targetCell;
    readonly ContactTerrainBandIndex _fringe;
    readonly NavGrid _targetGrid;
    readonly NavLink _link;
    internal NavAreaFootprint Ground { get; }
    internal ContactTerrainDropCorridor Corridor { get; }
    internal Vector3 From { get; }
    internal Vector3 To { get; }

    ContactTerrainDropCertificate(ContactTerrainDropCertificate source)
    {
        _evidence = source._evidence; _keys = source._keys; _upper = source._upper;
        _steep = new(source._steep); _landing = source._landing;
        _sourceCell = source._sourceCell; _targetCell = source._targetCell; _fringe = source._fringe;
        _targetGrid = source._targetGrid; _link = source._link;
        Ground = source.Ground; Corridor = source.Corridor; From = source.From; To = source.To;
    }
    internal ContactTerrainDropCertificate WithoutSteepForTests(MovementSurfaceFeatureKey key)
    {
        var copy = new ContactTerrainDropCertificate(this);
        if (copy._keys.TryGetValue(key, out int face)) copy._steep.Remove(face);
        return copy;
    }

    ContactTerrainDropCertificate(PhysicsNavDescentEvidence evidence, NavAreaFootprint ground, MoveTuning tuning,
        NavSpace space, NavLink link, Vector3 from, Vector3 to, float allowance, HashSet<int> faces,
        HashSet<int> upper, HashSet<int> steep, HashSet<int> landing, int sourceFace, int targetFace)
    {
        _evidence = evidence; Ground = ground; From = from; To = to; _link = link;
        _targetGrid = space.Layers[link.ToLayer];
        _upper = upper; _steep = steep; _landing = landing;
        _sourceCell = Component(evidence, [sourceFace], upper.Intersect(evidence.Column(link.FromX, link.FromZ).ToArray().Select(c => c.Face)).ToHashSet());
        _targetCell = Component(evidence, [targetFace], landing.Intersect(evidence.Column(link.ToX, link.ToZ).ToArray().Select(c => c.Face)).ToHashSet());
        _fringe = new(evidence, [steep.ToArray()], tuning);
        Corridor = new(evidence, ground, tuning, from, to, allowance);
        foreach (int face in faces) _keys.Add(evidence.Face(face).Key, face);
    }

    internal static bool HasSteep(PhysicsNavDescentEvidence evidence, MoveTuning tuning, Vector3 from, Vector3 to) =>
        ProjectedFaces(evidence, tuning, from, to).Any(i => evidence.Face(i).Analytic &&
            evidence.Face(i).Normal.Y > 0 && evidence.Face(i).Normal.Y < MathF.Cos(tuning.MaxSlopeRadians));

    internal static ContactTerrainDropCertificate? Create(PhysicsNavDescentEvidence evidence,
        ContactTerrainProfileEvidence sources, NavAreaFootprint ground, MoveTuning tuning, NavSpace space,
        NavLink link, Vector3 from, Vector3 to, float allowance)
    {
        if (space.Layers.Any(grid => grid.Width != evidence.Width || grid.Height != evidence.Height ||
                grid.CellSize != evidence.Options.CellSize || grid.OriginX != evidence.Options.MinX || grid.OriginZ != evidence.Options.MinZ) ||
            !sources.TryGet(link.FromLayer, link.FromX, link.FromZ, out var source) ||
            !sources.TryGet(link.ToLayer, link.ToX, link.ToZ, out var target) ||
            !new ContactTerrainDropCorridor(evidence, ground, tuning, from, to, allowance).Certify()) return null;
        var faces = ProjectedFaces(evidence, tuning, from, to);
        float cos = MathF.Cos(tuning.MaxSlopeRadians);
        var walkable = faces.Where(i => evidence.Face(i).Analytic && evidence.Face(i).Normal.Y >= cos).ToHashSet();
        var upper = Component(evidence, [source.Face], walkable);
        var landing = Component(evidence, [target.Face], walkable);
        if (upper.Count == 0 || landing.Count == 0) return null;
        var steepFaces = faces.Where(i => evidence.Face(i).Analytic && evidence.Face(i).Normal.Y > 0 &&
            evidence.Face(i).Normal.Y < cos && (double)evidence.Face(i).Normal.X * (to.X - (double)from.X) +
                (double)evidence.Face(i).Normal.Z * (to.Z - (double)from.Z) > 0).ToHashSet();
        var seeds = steepFaces.Where(i => evidence.Joins(i).ToArray().Any(upper.Contains)).ToArray();
        if (seeds.Length == 0) return null;
        var chain = Component(evidence, [seeds[0]], steepFaces);
        if (seeds.Any(i => !chain.Contains(i)) ||
            !chain.Any(i => evidence.Joins(i).ToArray().Any(landing.Contains))) return null;
        var certificate = new ContactTerrainDropCertificate(evidence, ground, tuning, space, link, from, to, allowance,
            faces, upper, chain, landing, source.Face, target.Face);
        return certificate.TargetInBand(sources, space, tuning, allowance) ? certificate : null;
    }

    internal bool TryFace(in ContactSupportEvidence support, out int index)
    {
        index = -1;
        if (support.Feature is not { } feature || !feature.IsValid || !support.Binding.IsValid ||
            support.Binding.Owner.WorldId != feature.Owner.WorldId || !_keys.TryGetValue(feature.Key, out index)) return false;
        var face = _evidence.Face(index);
        return face.Analytic && face.CanonicalOwner == feature.Owner && face.Points.Length == 3 &&
            Contains(feature.A) && Contains(feature.B) && Contains(feature.C);
        bool Contains(Vector3 p)
        {
            foreach (var q in face.Points) if (q.X == p.X && q.Y == p.Y && q.Z == p.Z) return true;
            return false;
        }
    }

    internal bool Upper(int face) => _upper.Contains(face);
    internal bool Steep(int face) => _steep.Contains(face);
    internal bool Source(int face) => _sourceCell.Contains(face);
    internal bool Landing(int face, Vector3 feet)
    {
        if (!_landing.Contains(face) || !Corridor.Contains(feet)) return false;
        var o = _evidence.Options;
        int x = (int)Math.Floor((feet.X - (double)o.MinX) / o.CellSize);
        int z = (int)Math.Floor((feet.Z - (double)o.MinZ) / o.CellSize);
        return _fringe.Covers(x, z) || x == _link.ToX && z == _link.ToZ;
    }
    internal bool Terminal(int face, Vector3 feet)
    {
        var cell = _targetGrid.CellOf(feet.X, feet.Z);
        return _targetCell.Contains(face) && cell.X == _link.ToX && cell.Z == _link.ToZ &&
            Ground.Accepts(feet) && GroundTraversalProbe.Near(feet, To);
    }

    bool TargetInBand(ContactTerrainProfileEvidence sources, NavSpace space, MoveTuning tuning, float allowance)
    {
        int x = _link.ToX - _link.FromX, z = _link.ToZ - _link.FromZ;
        int distance = Math.Max(Math.Abs(x), Math.Abs(z)), dx = Math.Sign(x), dz = Math.Sign(z);
        if (distance == 0 || x != dx * distance || z != dz * distance) return false;
        int direction = Enumerable.Range(0, 8).Single(i => NavTraversalLayer.NeighborX(i) == dx && NavTraversalLayer.NeighborZ(i) == dz);
        if (distance > _fringe.Reach(_link.FromX, _link.FromZ, direction)) return false;
        for (int d = 1; d <= distance; d++)
            for (int layer = 0; layer < space.Layers.Count; layer++)
            {
                int nx = _link.FromX + dx * d, nz = _link.FromZ + dz * d;
                if (!sources.TryGet(layer, nx, nz, out _)) continue;
                double drop = (double)From.Y - space.Layers[layer].RestingHeightAt(nx, nz)!.Value;
                if (Math.Abs(drop) <= tuning.StepHeight || d < distance && drop > tuning.StepHeight && drop <= allowance)
                    return false;
            }
        return true;
    }

    static HashSet<int> Component(PhysicsNavDescentEvidence evidence, IEnumerable<int> seeds, HashSet<int> allowed)
    {
        var result = seeds.Where(allowed.Contains).ToHashSet();
        var queue = new Queue<int>(result);
        while (queue.TryDequeue(out int next))
            foreach (int joined in evidence.Joins(next))
                if (allowed.Contains(joined) && result.Add(joined)) queue.Enqueue(joined);
        return result;
    }
    static HashSet<int> ProjectedFaces(PhysicsNavDescentEvidence evidence, MoveTuning tuning, Vector3 from, Vector3 to)
    {
        var o = evidence.Options;
        double radius = 2d * tuning.CapsuleRadius;
        int Cell(double p, float origin, int count) => (int)Math.Clamp(Math.Floor((p - origin) / o.CellSize), 0, count - 1);
        int x0 = Cell(Math.Min(from.X, to.X) - radius, o.MinX, evidence.Width);
        int x1 = Cell(Math.Max(from.X, to.X) + radius, o.MinX, evidence.Width);
        int z0 = Cell(Math.Min(from.Z, to.Z) - radius, o.MinZ, evidence.Height);
        int z1 = Cell(Math.Max(from.Z, to.Z) + radius, o.MinZ, evidence.Height);
        var result = new HashSet<int>();
        for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
        {
            if (!NavFootprintGeometry.Touches(from, to, o.MinX + x * (double)o.CellSize,
                    o.MinZ + z * (double)o.CellSize, o.CellSize, radius)) continue;
            foreach (var face in evidence.Column(x, z)) result.Add(face.Face);
        }
        return result;
    }
}
