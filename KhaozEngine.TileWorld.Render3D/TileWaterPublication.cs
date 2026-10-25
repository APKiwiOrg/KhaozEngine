using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.TileWorld;

/// <summary>A refused optical request. Its preparation stays owned, so an awaited source arrival can retry it
/// within that source's publication group, without a remesh.</summary>
internal sealed record TileWaterRefusal(TileWaterPreparation Preparation, TileWaterOpticsFailure Failure,
    TileWaterSourceStamp? Availability);

/// <summary>Owns resident optical generations, refusals, their diagnostics and dependency metadata. A source
/// replacement stages every dependent field against one candidate table, uploads all of them, validates again
/// and swaps the complete group at once. Any upload failure discards the whole group.</summary>
internal sealed class TileWaterPublication
{
    readonly ITileWorldWaterTriangleOpticsScene? _scene;
    readonly Dictionary<(RegionCoord Region, int Plane), TileWaterOpticsFailure> _failures = new();
    readonly List<(RegionCoord Region, int Plane)> _dependents = new();
    IReadOnlyList<TileWaterOpticsFailure> _failureSnapshot = Array.Empty<TileWaterOpticsFailure>();

    internal TileWaterPublication(ITileWorldWaterTriangleOpticsScene? scene) => _scene = scene;

    internal Dictionary<(RegionCoord Region, int Plane), TileWaterGeneration> Resident { get; } = new();
    internal Dictionary<(RegionCoord Region, int Plane), TileWaterRefusal> Refused { get; } = new();
    internal TileWaterDependencyRegistry Registry { get; } = new();
    internal IReadOnlyList<TileWaterOpticsFailure> Failures => _failureSnapshot;
    internal int FailureCount => _failures.Count;
    internal bool SweepDue { get; set; }

    internal sealed class Member((RegionCoord Region, int Plane) key, TileWaterPreparation preparation, object? previous,
        bool own)
    {
        internal (RegionCoord Region, int Plane) Key { get; } = key;
        internal TileWaterPreparation Preparation { get; } = preparation;
        internal object? Previous { get; } = previous;
        internal bool Own { get; } = own;
        internal TileWaterTriangleOutcome Outcome { get; set; } = TileWaterTriangleOutcome.Empty;
        internal TileWaterGeneration? Staged { get; set; }
        internal bool Dropped { get; set; }
    }

    /// <summary>One staged source replacement. Mesh and Ground belong to the view's half of the swap.</summary>
    internal sealed class Group
    {
        readonly bool _changesSource;
        readonly TileGroundSurfaceSnapshot? _candidate;
        readonly Func<RegionCoord, int, TileGroundSurfaceSnapshot?> _accepted;

        internal Group(RegionCoord region, int plane, bool changesSource, TileGroundSurfaceSnapshot? candidate,
            Func<RegionCoord, int, TileGroundSurfaceSnapshot?> accepted)
        {
            Region = region;
            Plane = plane;
            _changesSource = changesSource;
            _candidate = candidate;
            _accepted = accepted;
            Table = Source;
        }

        internal RegionCoord Region { get; }
        internal int Plane { get; }
        internal List<Member> Members { get; } = new();
        internal TileGroundCpuBuild? Ground { get; set; }
        internal MeshHandle? Mesh { get; set; }
        // The one candidate table: this transaction's candidate overlays the accepted sources.
        internal Func<RegionCoord, TileGroundSurfaceSnapshot?> Table { get; }

        TileGroundSurfaceSnapshot? Source(RegionCoord region) =>
            _changesSource && region == Region ? _candidate : _accepted(region, Plane);
    }

    object? Previous((RegionCoord Region, int Plane) key) =>
        Resident.TryGetValue(key, out TileWaterGeneration? generation) ? generation
        : Refused.TryGetValue(key, out TileWaterRefusal? refusal) ? refusal : null;

    internal Group Stage(RegionCoord region, int plane, bool changesSource, TileGroundSurfaceSnapshot? candidate,
        TileWaterPreparation? own, Func<RegionCoord, int, TileGroundSurfaceSnapshot?> accepted,
        in TileWaterSourceContext context)
    {
        var group = new Group(region, plane, changesSource, candidate, accepted);
        var key = (region, plane);
        if (own is not null) group.Members.Add(new Member(key, own, Previous(key), own: true));
        if (changesSource && _scene is not null)
        {
            _dependents.Clear();
            Registry.CollectDependents(key, _dependents);
            foreach ((RegionCoord Region, int Plane) dependent in _dependents)
            {
                if (dependent == key) continue;
                if (Resident.TryGetValue(dependent, out TileWaterGeneration? generation))
                    group.Members.Add(new Member(dependent, generation.Preparation, generation, own: false));
                else if (Refused.TryGetValue(dependent, out TileWaterRefusal? refusal))
                    group.Members.Add(new Member(dependent, refusal.Preparation, refusal, own: false));
            }
        }
        try
        {
            foreach (Member member in group.Members)
            {
                TileWaterPreparation preparation = member.Preparation;
                if (preparation.Uncertified && preparation.Planes.Length > 0)
                    member.Outcome = TileWaterTriangleOutcome.Empty.Refused(new TileWaterOpticsFailure(
                        member.Key.Region, member.Key.Plane, preparation.Planes[0].SurfaceY,
                        TileWaterOpticsFailureKind.UncertifiedDisplacement));
                else if (_scene is not null)
                    member.Outcome = TileWaterTrianglePreparation.Prepare(preparation, member.Key.Region,
                        member.Key.Plane, group.Table, context);
                if (member.Outcome.Failure is not null) continue;
                try
                {
                    member.Staged = new TileWaterGeneration(member.Preparation, member.Outcome, _scene);
                }
                catch (TileWaterFieldRefusedException refused)
                {
                    // The backend refused a scalar before allocating. That refuses this member, not the group.
                    member.Outcome = member.Outcome.Refused(new TileWaterOpticsFailure(member.Key.Region,
                        member.Key.Plane, refused.Field.Level, TileWaterTrianglePreparation.Kind(refused.Reason),
                        Domain: refused.Field.Domain));
                }
            }
        }
        catch
        {
            Discard(group);
            throw;
        }
        return group;
    }

    /// <summary>The second validation, after every upload and immediately before the swap.</summary>
    internal void Revalidate(Group group, Func<TileWaterPreparation, bool> inputsCurrent, in TileWaterSourceContext context)
    {
        foreach (Member member in group.Members)
        {
            if (member.Dropped) continue;
            bool current = inputsCurrent(member.Preparation) &&
                (member.Own || ReferenceEquals(Previous(member.Key), member.Previous)) &&
                (member.Outcome.Failure is not null ||
                    TileWaterTrianglePreparation.SourcesCurrent(member.Outcome, group.Table, context));
            if (current) continue;
            member.Dropped = true;
            member.Staged?.Dispose();
            member.Staged = null;
        }
    }

    internal void Discard(Group group)
    {
        foreach (Member member in group.Members)
        {
            member.Staged?.Dispose();
            member.Staged = null;
        }
    }

    /// <summary>Swap every member at once. Retired generations are released only after all replacements are
    /// resident. A dropped member withdraws and asks for a rebuild.</summary>
    internal void Commit(Group group, Action<RegionCoord, int> queue)
    {
        var retired = new List<TileWaterGeneration>();
        foreach (Member member in group.Members)
        {
            (RegionCoord Region, int Plane) key = member.Key;
            if (!member.Own && !ReferenceEquals(Previous(key), member.Previous))
            {
                member.Staged?.Dispose();
                continue;
            }
            Remove(key, retired);
            if (member.Dropped)
            {
                queue(key.Region, key.Plane);
                continue;
            }
            Registry.Register(key, member.Outcome.SourceKeys);
            if (member.Staged is { } staged)
            {
                Resident[key] = staged;
                ForgetFailure(key);
                continue;
            }
            TileWaterOpticsFailure failure = member.Outcome.Failure!.Value;
            Refused[key] = new TileWaterRefusal(member.Preparation, failure, member.Outcome.Availability);
            RecordFailure(key, failure);
        }
        foreach (TileWaterGeneration generation in retired) generation.Dispose();
        SweepDue = true;
    }

    void Remove((RegionCoord Region, int Plane) key, List<TileWaterGeneration>? retired)
    {
        if (Resident.Remove(key, out TileWaterGeneration? old))
        {
            if (retired is null) old.Dispose();
            else retired.Add(old);
        }
        Refused.Remove(key);
        Registry.Unregister(key);
        SweepDue = true;
    }

    /// <summary>Withdraw resident or refused water. The diagnostic remains until the rebuild decides.</summary>
    internal void Withdraw((RegionCoord Region, int Plane) key) => Remove(key, null);

    internal void Release((RegionCoord Region, int Plane) key)
    {
        Remove(key, null);
        ForgetFailure(key);
    }

    /// <summary>An accepted surface left the table. Its resident dependents cannot keep a field over a bed that is
    /// no longer drawn. They become refusals awaiting that source, retaining their registration.</summary>
    internal void SourceRemoved((RegionCoord Region, int Plane) source)
    {
        _dependents.Clear();
        Registry.CollectDependents(source, _dependents);
        foreach ((RegionCoord Region, int Plane) dependent in _dependents)
        {
            if (!Resident.Remove(dependent, out TileWaterGeneration? generation)) continue;
            generation.Dispose();
            TileWaterPreparation preparation = generation.Preparation;
            var failure = new TileWaterOpticsFailure(dependent.Region, dependent.Plane,
                preparation.Planes.Length > 0 ? preparation.Planes[0].SurfaceY : 0f,
                TileWaterOpticsFailureKind.MissingSurfaceData, source.Region);
            Refused[dependent] = new TileWaterRefusal(preparation, failure, generation.Availability);
            RecordFailure(dependent, failure);
        }
    }

    internal void ReleaseAll()
    {
        foreach (TileWaterGeneration generation in Resident.Values) generation.Dispose();
        Resident.Clear();
        Refused.Clear();
        Registry.Clear();
        SweepDue = false;
        if (_failures.Count == 0) return;
        _failures.Clear();
        RebuildFailures();
    }

    /// <summary>Release every epoch not owned by a resident generation, refusal or one of the pending stamps.</summary>
    internal void Sweep(IEnumerable<TileWaterSourceStamp> pending)
    {
        SweepDue = false;
        Registry.Sweep(Live(pending));
    }

    IEnumerable<TileWaterSourceStamp> Live(IEnumerable<TileWaterSourceStamp> pending)
    {
        foreach (TileWaterGeneration generation in Resident.Values)
        {
            yield return generation.Preparation.Stamp;
            if (generation.Availability is { } availability) yield return availability;
        }
        foreach (TileWaterRefusal refusal in Refused.Values)
        {
            yield return refusal.Preparation.Stamp;
            if (refusal.Availability is { } availability) yield return availability;
        }
        foreach (TileWaterSourceStamp stamp in pending) yield return stamp;
    }

    internal void RecordFailure((RegionCoord Region, int Plane) key, TileWaterOpticsFailure failure)
    {
        if (_failures.TryGetValue(key, out TileWaterOpticsFailure old) && old == failure) return;
        _failures[key] = failure;
        RebuildFailures();
    }

    internal void ForgetFailure((RegionCoord Region, int Plane) key)
    {
        if (_failures.Remove(key)) RebuildFailures();
    }

    void RebuildFailures() => _failureSnapshot = Array.AsReadOnly(
        new List<TileWaterOpticsFailure>(_failures.Values).ToArray());
}
