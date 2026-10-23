using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using static KhaozEngine.Physics.ShapeQueryMath;

namespace KhaozEngine.MapDoc.Render3D;

public enum MapPresentationChannel { Selection, Camera, Sky, Physical }
public readonly record struct MapPresentationRay(MapFramePoint Origin, Vector3 Direction, double MaxDistance);
/// <summary>LocalPoint is relative to the supplied frame's horizontal origin. Y remains world datum.</summary>
public readonly record struct MapPresentationHit(string OwnerId, long? NumericId, double Distance, Vector3 LocalPoint, Vector3 Normal);
public interface IMapPresentationCut { bool Accept(string ownerId, MapFramePoint point); }

/// <summary>Ordered reusable candidate traversal over a prepared plan. Each picker belongs to one serial frame consumer.</summary>
public sealed class MapPresentationPicker
{
    readonly MapRenderPlan _plan;
    readonly IMapPresentationCut? _cut;
    readonly Workspace _scratch;
    internal sealed class Workspace(int capacity)
    {
        internal readonly Candidate[] Candidates = new Candidate[capacity];
        internal readonly int[] Visited = new int[capacity];
        internal int Epoch;
    }
    internal readonly record struct Candidate(int Index, double Entry, string Owner);
    static readonly Comparison<Candidate> s_order = static (a, b) =>
    { int order = a.Entry.CompareTo(b.Entry); return order != 0 ? order : string.CompareOrdinal(a.Owner, b.Owner); };
    public MapPresentationPicker(MapRenderPlan plan, IMapPresentationCut? cut = null)
    {
        ArgumentNullException.ThrowIfNull(plan); _plan = plan; _cut = cut;
        _scratch = plan.PresentationGeometry.PickWorkspace;
    }
    public MapPresentationHit? Pick(MapPresentationRay ray, MapPresentationChannel channel, MapFrameState frame)
    {
        if (channel is < MapPresentationChannel.Selection or > MapPresentationChannel.Physical) throw new ArgumentOutOfRangeException(nameof(channel));
        var direction = ray.Direction;
        var origin = ray.Origin;
        double x = (long)origin.Frame.X * (long)WorldFrame.Grid + (double)origin.Local.X;
        double z = (long)origin.Frame.Z * (long)WorldFrame.Grid + (double)origin.Local.Z;
        var (o, d) = ShapeRayQueries.Validate(new(x, origin.Local.Y, z, direction.X, direction.Y, direction.Z, ray.MaxDistance));
        _plan.Update(frame);
        if (_scratch.Epoch == int.MaxValue) { Array.Clear(_scratch.Visited); _scratch.Epoch = 0; }
        _scratch.Epoch++;
        var geometry = _plan.PresentationGeometry; int count = 0;
        for (int i = 0; i < geometry.Placements.Count; i++)
        {
            var row = geometry.Placements[i];
            var box = channel == MapPresentationChannel.Selection ? row.SelectionBounds : row.ColliderBounds;
            if (box is not { } b) continue;
            double enter = 0, end = ray.MaxDistance;
            var local = o.Subtract(new(row.Anchor.X, row.Anchor.Y, row.Anchor.Z));
            if (ClipRay(local.X, d.X, b.MinX, b.MaxX, 1d / 1024, ref enter, ref end) &&
                ClipRay(local.Y, d.Y, b.MinY, b.MaxY, 1d / 1024, ref enter, ref end) &&
                ClipRay(local.Z, d.Z, b.MinZ, b.MaxZ, 1d / 1024, ref enter, ref end))
                _scratch.Candidates[count++] = new(i, enter, row.OwnerId);
        }
        // Canonical faces are already prepared in stable face-key order. No paint partition changes these hits.
        if (channel != MapPresentationChannel.Selection)
            for (int i = 0; i < geometry.Faces.Length; i++)
                _scratch.Candidates[count++] = new(geometry.Placements.Count + i, 0, geometry.Faces[i].OwnerId);
        _scratch.Candidates.AsSpan(0, count).Sort(s_order);
        MapPresentationHit? best = null;
        for (int c = 0; c < count; c++)
        {
            var candidate = _scratch.Candidates[c];
            if (best is { } known && candidate.Entry > known.Distance) break;
            if (_scratch.Visited[candidate.Index] == _scratch.Epoch) continue;
            _scratch.Visited[candidate.Index] = _scratch.Epoch;
            ShapeRayHit hit; string owner; long? numeric;
            if (candidate.Index < geometry.Placements.Count)
            {
                var row = geometry.Placements[candidate.Index]; owner = row.OwnerId; numeric = row.NumericId;
                if (!AcceptRoof(owner, channel)) continue;
                var local = o.Subtract(new(row.Anchor.X, row.Anchor.Y, row.Anchor.Z));
                if (!row.Raycast(new(local.X, local.Y, local.Z, direction.X, direction.Y, direction.Z, ray.MaxDistance),
                    channel == MapPresentationChannel.Selection, out hit)) continue;
            }
            else
            {
                var face = geometry.Faces[candidate.Index - geometry.Placements.Count]; owner = face.OwnerId; numeric = null;
                if (!CastFace(face, o, d, ray.MaxDistance, out hit)) continue;
            }
            if (best is { } previous && (hit.Distance > previous.Distance || hit.Distance == previous.Distance && string.CompareOrdinal(owner, previous.OwnerId) >= 0)) continue;
            var point = o.Add(d.Scale(hit.Distance));
            var localPoint = new Vector3((float)(point.X - (long)frame.Frame.X * (long)WorldFrame.Grid), (float)point.Y,
                (float)(point.Z - (long)frame.Frame.Z * (long)WorldFrame.Grid));
            if (_cut?.Accept(owner, new(frame.Frame, localPoint)) == false) continue;
            best = new(owner, numeric, hit.Distance, localPoint, hit.Normal);
        }
        return best;
    }
    bool AcceptRoof(string owner, MapPresentationChannel channel)
    {
        var roof = _plan.Inventory.Placements.RoofIntent(owner);
        if (roof is null)
        {
            if (!_plan.RoofIndices.TryGetValue(owner, out int index)) return true;
            roof = _plan.RoofEvaluation.Roofs[index];
        }
        return channel switch
        {
            MapPresentationChannel.Selection => roof.ColourVisible,
            MapPresentationChannel.Camera => roof.CameraBoomBlocks,
            MapPresentationChannel.Sky => roof.SkyPickingBlocks,
            _ => true
        };
    }
    static bool CastFace(MapPresentationFace f, ShapeDouble3 o, ShapeDouble3 d, double end, out ShapeRayHit hit)
    {
        hit = default;
        var a = new ShapeDouble3(f.Ax, f.Ay, f.Az); var e1 = new ShapeDouble3(f.Bx - f.Ax, f.By - f.Ay, f.Bz - f.Az);
        var e2 = new ShapeDouble3(f.Cx - f.Ax, f.Cy - f.Ay, f.Cz - f.Az); var p = ShapeDouble3.Cross(d, e2);
        double determinant = Dot(e1, p); if (determinant == 0) return false;
        double inverse = 1 / determinant; var s = o.Subtract(a); double u = Dot(s, p) * inverse;
        if (u < 0 || u > 1) return false;
        var q = ShapeDouble3.Cross(s, e1); double v = Dot(d, q) * inverse;
        if (v < 0 || u + v > 1) return false;
        double distance = Dot(e2, q) * inverse; if (distance < 0 || distance > end) return false;
        var normal = ShapeDouble3.Cross(e1, e2); if (Dot(normal, d) > 0) normal = normal.Scale(-1);
        hit = new(distance, Unit(normal), false); return true;
    }
}
