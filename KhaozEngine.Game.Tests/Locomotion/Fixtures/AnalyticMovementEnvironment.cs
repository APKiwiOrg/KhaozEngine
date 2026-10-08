using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Fixtures;

// Finite test producer only. The geometry family is clipped axis-aligned boxes, bounded unions
// and explicitly declared connections. Uncertifiable coverage refuses instead of inventing dry space.
internal sealed class AnalyticMovementEnvironment : IDisposable
{
    internal readonly record struct Room(string Id, AnalyticBox Bounds);
    internal readonly record struct Water(string Domain, string Room, AnalyticBox Bounds, float SurfaceY);
    internal readonly record struct Link(string A, string B);
    readonly Room[] _rooms;
    readonly Water[] _water;
    readonly Link[] _links;
    IPhysicsWorldQueryView? _view;
    public BepuPhysicsWorld Physics { get; } = new(Vector3.Zero);
    public bool MissingContainment;
    public bool CertifyAxisSeparation;
    public string[] BackingIds = ["fixture-page"];
    public EnvironmentAcquisitionFixture Acquisition { get; private set; } = null!;
    public static readonly MovementQueryIdentity Identity = new("closure", 1u, "scope");
    const float Skin = MovementQueryLease.CoverageSkinMetres;
    const float Error = 0.00001f;

    public AnalyticMovementEnvironment(Room[] rooms, Water[] water, Link[]? links = null)
    {
        if (rooms.Length > 8 || water.Length > 8 || (links?.Length ?? 0) > 8)
            throw new ArgumentException("The analytic fixture has eight-entry limits.");
        _rooms = (Room[])rooms.Clone();
        _links = links is null ? [] : (Link[])links.Clone();
        for (int i = 0; i < _rooms.Length; i++)
            for (int j = i + 1; j < _rooms.Length; j++)
                if (_rooms[i].Bounds.Clip(_rooms[j].Bounds).HasVolume)
                    throw new ArgumentException("The finite fixture requires nonoverlapping occupied rooms.");
        var cells = new List<Water>();
        foreach (Water source in water)
        {
            Room room = _rooms.Single(r => r.Id == source.Room);
            AnalyticBox clipped = source.Bounds.Clip(room.Bounds);
            if (clipped.HasVolume) cells.Add(source with { Bounds = clipped });
        }
        // Physical page regrouping does not manufacture a water boundary inside one semantic region.
        for (int i = 0; i < cells.Count; i++)
            for (int j = cells.Count - 1; j > i; j--)
                if (cells[i].Domain == cells[j].Domain && cells[i].Room == cells[j].Room &&
                    cells[i].SurfaceY == cells[j].SurfaceY && cells[i].Bounds.TryMerge(cells[j].Bounds, out var merged))
                {
                    cells[i] = cells[i] with { Bounds = merged };
                    cells.RemoveAt(j);
                }
        _water = cells.OrderBy(w => w.Domain, StringComparer.Ordinal).ThenBy(w => w.Room, StringComparer.Ordinal)
            .ThenBy(w => w.Bounds.Min.X).ThenBy(w => w.Bounds.Min.Y).ToArray();
    }

    public MovementQueryLease Acquire(string? room, float maxRise = 0f, float maxDrop = 0f, WorldFrame? worldFrame = null)
    {
        Assert.Null(_view);
        _view = Physics.CreateQueryViewExcludingStatics([]);
        var frame = new MovementFrameDescriptor(worldFrame ?? WorldFrame.Origin, Physics.Origin, 1ul);
        var scope = new MovementQueryScope(new Vector3(-32f), new Vector3(32f), maxRise, maxDrop,
            "world", room is null ? null : Space(room), Identity, frame);
        Acquisition = new EnvironmentAcquisitionFixture(_view, scope)
        {
            Resources = BackingIds,
            OnBodySample = Sample,
            OnCoverage = Trace,
            OnRebuild = Rebuild
        };
        var acquired = Acquisition.Acquire();
        Assert.Equal(MovementAvailability.Known, acquired.Status);
        return Assert.IsType<MovementQueryLease>(acquired.Lease);
    }

    public void Slab(float top, float halfThickness = 0.1f) => Physics.AddStatic(
        new BoxShape(new Vector3(8f, halfThickness, 8f)), Pose.At(new Vector3(0f, top - halfThickness, 0f)));

    MovementAvailability Rebuild(in FramedMovementState state, out MovementSelection selection)
    {
        selection = default;
        Assert.Null(state.Selection);
        if (MissingContainment) return MovementAvailability.Unresolved;
        Vector3 centre = state.State.Position;
        Room[] membership = _rooms.Where(room => room.Bounds.ContainsPoint(centre)).ToArray();
        if (membership.Length != 1) return MovementAvailability.Unresolved;
        // This producer reconstructs occupied space only. Standing support is selected separately.
        selection = new MovementSelection(Space(membership[0].Id), null, Identity);
        return MovementAvailability.Known;
    }

    MovementWaterPoint Sample(in MovementBodyQuery body)
    {
        if (MissingContainment) return UnknownPoint();
        Vector3 feet = body.Feet;
        Room[] membership = _rooms.Where(r => r.Bounds.ContainsPoint(feet)).ToArray();
        if (membership.Length != 1 || !Reachable(body.CurrentSpace.LocalId).Contains(membership[0].Id))
            return UnknownPoint();
        string selected = membership[0].Id;
        Water[] matches = _water.Where(w => w.Room == selected && w.Bounds.ContainsPoint(feet)).ToArray();
        if (matches.Length > 1) return UnknownPoint();
        if (matches.Length == 0) return new(MovementAvailability.Known, Space(selected), null, false, 1f, null);
        Water water = matches[0];
        return new(MovementAvailability.Known, Space(selected), Domain(water.Domain), true, 1f, Interval(water));
    }

    readonly record struct Hit(Water Water, float Enter, float Exit, Vector2 Column, Vector3 Normal, uint Handle);

    MovementCoverageResult Trace(in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans,
        Span<MovementDomainContact> contacts)
    {
        // The numerical certificate applies only to this finite fixture envelope and capsule family.
        if (MissingContainment || query.Body.Radius < 0.25f || query.Body.Radius > 0.252f ||
            query.Body.HalfHeight < 0.4f || query.Body.HalfHeight > 0.752f ||
            Vector3.Abs(query.Body.Centre).X > 16f || Vector3.Abs(query.Body.Centre).Y > 16f ||
            Vector3.Abs(query.Body.Centre).Z > 16f || Vector3.Abs(query.Delta).X > 8f ||
            Vector3.Abs(query.Delta).Y > 8f || Vector3.Abs(query.Delta).Z > 8f) return Refused();
        HashSet<string> reachable = Reachable(query.Body.CurrentSpace.LocalId);
        if (!KnownFeetPath(query, reachable)) return Refused();
        float radius = query.Body.Radius + Skin, halfHeight = query.Body.HalfHeight + Skin;
        var hits = new List<Hit>();
        var cuts = new SortedSet<float> { 0f, 1f };
        var union = new List<AnalyticBox>();
        for (int i = 0; i < _water.Length; i++)
        {
            Water water = _water[i];
            if (!reachable.Contains(water.Room)) continue;
            union.Add(water.Bounds);
            if (!water.Bounds.IntersectCapsule(query.Body.Centre, query.Delta, radius, halfHeight,
                out double first, out double last)) continue;
            float enter = (float)first, exit = (float)last;
            if (enter > first) enter = MathF.BitDecrement(enter);
            if (exit < last) exit = MathF.BitIncrement(exit);
            cuts.Add(enter);
            cuts.Add(exit);
            float boundaryAt = first == 0d && last < 1d ? exit : enter;
            Vector3 atEntry = query.Body.Centre + query.Delta * enter;
            Vector2 column = new(Math.Clamp(atEntry.X, water.Bounds.Min.X, water.Bounds.Max.X),
                Math.Clamp(atEntry.Z, water.Bounds.Min.Z, water.Bounds.Max.Z));
            hits.Add(new Hit(water, enter, exit, column, water.Bounds.ContactNormal(query.Body.Centre + query.Delta * boundaryAt,
                radius, halfHeight), (uint)i));
        }
        for (int i = 0; i < union.Count; i++)
            for (int j = union.Count - 1; j > i; j--)
                if (union[i].TryMerge(union[j], out var merged)) { union[i] = merged; union.RemoveAt(j); }
        Vector3 endCentre = query.Body.Centre + query.Delta;
        Vector3 extent = new(radius, halfHeight, radius);
        var envelope = new AnalyticBox(Vector3.Min(query.Body.Centre, endCentre) - extent,
            Vector3.Max(query.Body.Centre, endCentre) + extent);
        if (!AnalyticBoxComplement.TryCreate(envelope, union, out var dry)) return Refused();
        foreach (AnalyticBox box in dry)
            if (box.IntersectCapsule(query.Body.Centre, query.Delta, radius, halfHeight, out double enter, out double exit))
            { cuts.Add((float)enter); cuts.Add((float)exit); }

        var outputSpans = new List<MovementCoverageSpan>();
        var outputContacts = new List<MovementDomainContact>();
        float[] boundaries = cuts.ToArray();
        for (int i = 0; i < boundaries.Length; i++)
        {
            float start = boundaries[i];
            // Explicit zero-length spans preserve closed tangency, including both path endpoints.
            Append(start, start, query, radius, halfHeight, hits, dry, outputSpans, outputContacts);
            if (i + 1 < boundaries.Length)
                Append(start, boundaries[i + 1], query, radius, halfHeight, hits, dry, outputSpans, outputContacts);
        }
        if (outputSpans.Count > Math.Min(spans.Length, MovementQueryLease.MaxCoverageSpans) ||
            outputContacts.Count > Math.Min(contacts.Length, MovementQueryLease.MaxDomainContacts))
            return new(MovementAvailability.CapacityExceeded, 0, outputSpans.Count, 0, outputContacts.Count, Error, Identity);
        outputSpans.ToArray().AsSpan().CopyTo(spans);
        outputContacts.ToArray().AsSpan().CopyTo(contacts);
        return new(MovementAvailability.Known, outputSpans.Count, outputSpans.Count,
            outputContacts.Count, outputContacts.Count, Error, Identity);
    }

    void Append(float start, float end, in MovementMediumSweepQuery query, float radius, float halfHeight,
        List<Hit> hits, List<AnalyticBox> dry, List<MovementCoverageSpan> spans, List<MovementDomainContact> contacts)
    {
        float at = (start + end) * 0.5f;
        Vector3 centre = query.Body.Centre + query.Delta * at;
        bool hasDryCoverage = false;
        foreach (AnalyticBox box in dry)
            if (box.HasPositiveCapsuleOverlap(centre, radius, halfHeight)) { hasDryCoverage = true; break; }
        int first = contacts.Count;
        foreach (Hit hit in hits)
            if (at >= hit.Enter && at <= hit.Exit)
            {
                Vector3 contactCentre = query.Body.Centre + query.Delta * start;
                Vector2 column = new(Math.Clamp(contactCentre.X, hit.Water.Bounds.Min.X, hit.Water.Bounds.Max.X),
                    Math.Clamp(contactCentre.Z, hit.Water.Bounds.Min.Z, hit.Water.Bounds.Max.Z));
                bool overlap = hit.Water.Bounds.HasPositiveCapsuleOverlap(contactCentre,
                    query.Body.Radius + Error, query.Body.HalfHeight + Error);
                bool spanOverlap = start == end ? overlap : hit.Water.Bounds.IntersectCapsule(contactCentre,
                    query.Delta * (end - start), query.Body.Radius + Error, query.Body.HalfHeight + Error, out _, out _);
                if (CertifyAxisSeparation && SeparatedByAxis(query, hit.Water.Bounds))
                    overlap = spanOverlap = false;
                contacts.Add(new MovementDomainContact(Domain(hit.Water.Domain), Space(hit.Water.Room), Interval(hit.Water),
                    column, hit.Normal, start, BoundaryId(hit), hit.Handle,
                    overlap ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent,
                    spanOverlap ? MovementContactOverlap.Overlapping : MovementContactOverlap.Tangent));
            }
        spans.Add(new MovementCoverageSpan(start, end, first, contacts.Count - first, hasDryCoverage));
    }

    static bool SeparatedByAxis(in MovementMediumSweepQuery query, AnalyticBox box)
    {
        Vector3 c = query.Body.Centre, d = query.Delta;
        float r = query.Body.Radius, h = query.Body.HalfHeight;
        return Outside(c.X, r, d.X, box.Min.X, box.Max.X) ||
            Outside(c.Y, h, d.Y, box.Min.Y, box.Max.Y) || Outside(c.Z, r, d.Z, box.Min.Z, box.Max.Z);

        static bool Outside(float centre, float extent, float delta, float min, float max)
        {
            // Within this ratio, adding two binary32 inputs is exact in binary64. This optional
            // certificate proves an entire stationary/away path, including equality at a plane.
            if (centre != 0 && (Math.Abs(centre) < extent / 1024d || Math.Abs(centre) > extent * 1024d)) return false;
            return delta <= 0 && (double)centre + extent <= min || delta >= 0 && (double)centre - extent >= max;
        }
    }

    bool KnownFeetPath(in MovementMediumSweepQuery query, HashSet<string> reachable)
    {
        var intervals = new List<(double Start, double End)>();
        foreach (Room room in _rooms)
            if (reachable.Contains(room.Id) && room.Bounds.IntersectCapsule(query.Body.Feet, query.Delta, 0f, 0f,
                out double start, out double end)) intervals.Add((start, end));
        double through = 0d;
        foreach (var interval in intervals.OrderBy(i => i.Start))
        {
            if (interval.Start > through) return false;
            through = Math.Max(through, interval.End);
        }
        return through == 1d;
    }

    HashSet<string> Reachable(string start)
    {
        var result = new HashSet<string>(StringComparer.Ordinal) { start };
        for (int i = 0; i < _rooms.Length; i++)
            foreach (Link link in _links)
            {
                if (result.Contains(link.A)) result.Add(link.B);
                if (result.Contains(link.B)) result.Add(link.A);
            }
        return result;
    }

    static MovementWaterPoint UnknownPoint() => new(MovementAvailability.Unresolved, default, null, false, 0f, null);
    static string BoundaryId(Hit hit)
    {
        var faces = new List<string>();
        if (hit.Normal.X != 0f) faces.Add(hit.Normal.X < 0f ? "x-min" : "x-max");
        if (hit.Normal.Y != 0f) faces.Add(hit.Normal.Y < 0f ? "y-min" : "y-max");
        if (hit.Normal.Z != 0f) faces.Add(hit.Normal.Z < 0f ? "z-min" : "z-max");
        return hit.Water.Domain + "/" + string.Join("+", faces);
    }
    static MovementCoverageResult Refused() => new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0f, Identity);
    static MovementWaterInterval Interval(Water water) => new(water.Bounds.Min.Y, water.Bounds.Max.Y,
        water.SurfaceY, water.Bounds.Max.Y == water.SurfaceY, water.Domain + "/floor", water.Domain + "/upper");
    public static MovementSpaceKey Space(string name) => new("world", name);
    public static MovementDomainKey Domain(string name) => new("world", name);
    public void Dispose() { _view?.Dispose(); Physics.Dispose(); }
}
