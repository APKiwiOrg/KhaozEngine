using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Water;

/// <summary>The reads one containment run spent on connectivity: relation records visited and distinct relation
/// endpoints resolved to their alias owners.</summary>
internal readonly record struct MapWaterConnectivityReads(int Relations, int Endpoints)
{
    internal int Constructions { get; init; }
}

/// <summary>Cumulative graph construction and read work owned by one whole containment call.</summary>
internal sealed class MapWaterConnectivityWork
{
    internal MapWaterConnectivityReads Reads { get; private set; }

    internal void Add(int relations, int endpoints) => Reads = new(Reads.Relations + relations, Reads.Endpoints + endpoints)
    {
        Constructions = Reads.Constructions + 1,
    };
}

/// <summary>The portal and vertical link edges of one view, built in one pass over its acquired relation records and
/// shared by every body of a containment run. An edge is one portal or one opening of a link. Each endpoint is
/// normalized through its own alias chain once per run, and an edge's exact aperture bottom is compiled once, the first
/// time a body needs it. A view holds only the relations it acquired, so a missing edge proves nothing unless the view
/// lists the whole document.</summary>
internal sealed class MapWaterConnectivity
{
    readonly MapScopedSurfaces _view;
    readonly List<Edge> _edges = new();
    readonly Dictionary<string, List<int>> _byOwner = new(StringComparer.Ordinal);
    readonly List<int> _unresolved = new();
    readonly Dictionary<MapRecordRef, Endpoint> _endpoints = new();
    int _relations;

    internal MapWaterConnectivity(MapScopedSurfaces view, MapWaterConnectivityWork? work = null)
    {
        _view = view;
        foreach (MapRecordRef reference in view.Witness.Records.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Anchor))
        {
            if (!view.TryRecord(reference, out MapTopologyRecord? record, out _, borrow: true)) continue;
            if (record is MapCavePortal portal)
            {
                _relations++;
                Add(reference, portal.FromSpace, portal.ToSpace, null, $"portal '{reference.Id}'");
            }
            else if (record is MapVerticalLink link)
            {
                _relations++;
                foreach (MapRecordRef opening in link.Openings ?? Array.Empty<MapRecordRef>())
                    if (opening is not null)
                        Add(opening, link.UpperSpace, link.LowerSpace, reference, $"link '{reference.Id}' opening '{opening.Id}'");
            }
        }
        work?.Add(_relations, _endpoints.Count);
    }

    /// <summary>Whether <paramref name="covered"/> forms one set over the edges joining two of its spaces whose exact
    /// aperture bottom is strictly below <paramref name="surface"/>. An edge with one endpoint outside the set is
    /// skipped, and <paramref name="read"/> collects the record anchors of every edge evaluated. An edge whose geometry
    /// is definitely invalid adds an error under <paramref name="label"/> and joins nothing. Returns why an edge that
    /// could join the set could not be evaluated, or null.</summary>
    internal string? Join(string label, MapExactValue surface, IReadOnlyList<string> covered, List<string> errors, out bool connected,
        ISet<MapPatchKey>? read = null)
    {
        var adjacency = covered.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        var candidates = new SortedSet<int>(_unresolved);
        foreach (string id in covered)
            if (_byOwner.TryGetValue(id, out List<int>? indices)) candidates.UnionWith(indices);
        string? unknown = null;
        foreach (int index in candidates) Evaluate(_edges[index]);

        var reached = new HashSet<string>(StringComparer.Ordinal) { covered[0] };
        var pending = new Queue<string>(reached);
        while (pending.TryDequeue(out string? current))
            foreach (string next in adjacency[current])
                if (reached.Add(next)) pending.Enqueue(next);
        connected = reached.Count == adjacency.Count;
        return unknown;

        void Evaluate(Edge edge)
        {
            string? a = edge.From.Owner?.Id, b = edge.To.Owner?.Id;
            if ((a is not null && !adjacency.ContainsKey(a)) || (b is not null && !adjacency.ContainsKey(b))) return;
            read?.Add(edge.Aperture.Anchor);
            if (edge.Link is { } link) read?.Add(link.Anchor);
            if (edge.From.Error is { } fromError && edge.To.Error is not null)
            {
                // With both endpoints unresolved the edge may join any spaces, so it leaves every disconnected body unknown.
                unknown ??= $"{edge.Relation} (space '{edge.FromRef.Id}' {fromError.Message})";
                return;
            }
            if ((edge.From.Error ?? edge.To.Error) is { } error)
            {
                Fail($"space '{(edge.From.Error is null ? edge.ToRef : edge.FromRef).Id}'", error);
                return;
            }
            Bottom(edge);
            if (edge.Overflow) unknown ??= $"{edge.Relation} is not representable";
            else if (edge.Error is { } apertureError) Fail(edge.Relation, apertureError);
            else if (edge.BottomY!.Value.CompareTo(surface) < 0)
            {
                adjacency[a!].Add(b!);
                adjacency[b!].Add(a!);
            }
        }

        void Fail(string what, MapDocumentException error)
        {
            if (MapWaterContainment.Invalid(error)) errors.Add($"{label}: {what} is invalid ({error.Message})");
            else unknown ??= $"{what} ({error.Message})";
        }
    }

    void Add(MapRecordRef aperture, MapRecordRef? from, MapRecordRef? to, MapRecordRef? link, string relation)
    {
        if (from is null || to is null) return;
        Endpoint a = Resolve(from), b = Resolve(to);
        if (a.Owner is not null && b.Owner is not null && a.Owner.Id == b.Owner.Id) return;
        int index = _edges.Count;
        _edges.Add(new Edge(aperture, link, from, to, a, b, relation));
        if (a.Owner is null && b.Owner is null) _unresolved.Add(index);
        foreach (MapRecordRef? owner in new[] { a.Owner, b.Owner })
            if (owner is not null)
            {
                if (!_byOwner.TryGetValue(owner.Id, out List<int>? indices)) _byOwner.Add(owner.Id, indices = new List<int>());
                indices.Add(index);
            }
    }

    /// <summary>The space record an endpoint names, followed through its alias chain to the owner's reference.</summary>
    Endpoint Resolve(MapRecordRef endpoint)
    {
        if (_endpoints.TryGetValue(endpoint, out Endpoint? known)) return known;
        Endpoint result;
        try
        {
            MapRecordRef owner = endpoint;
            MapSpaceDoc space = MapSpaceGeometry.Record<MapSpaceDoc>(_view, endpoint);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (space.AliasOf is { } alias)
            {
                if (!seen.Add(space.Id)) throw new MapDocumentException("invalid space alias cycle");
                owner = alias;
                space = MapSpaceGeometry.Record<MapSpaceDoc>(_view, alias);
            }
            result = new(owner, null);
        }
        catch (MapDocumentException error) { result = new(null, error); }
        _endpoints.Add(endpoint, result);
        return result;
    }

    /// <summary>Compiles the edge's aperture between its endpoint owners once and keeps its exact bottom or failure.</summary>
    void Bottom(Edge edge)
    {
        if (edge.Compiled) return;
        edge.Compiled = true;
        try
        {
            MapPortalFact fact = MapRelationApertures.Compile(_view, edge.Aperture, edge.From.Owner!, edge.To.Owner!, edge.Link);
            edge.BottomY = MapRelationApertures.BottomOf(fact);
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException) { edge.Overflow = true; }
        catch (MapDocumentException error) { edge.Error = error; }
    }

    sealed record Endpoint(MapRecordRef? Owner, MapDocumentException? Error);

    sealed class Edge(MapRecordRef aperture, MapRecordRef? link, MapRecordRef fromRef, MapRecordRef toRef, Endpoint from, Endpoint to,
        string relation)
    {
        internal MapRecordRef Aperture { get; } = aperture;
        internal MapRecordRef? Link { get; } = link;
        internal MapRecordRef FromRef { get; } = fromRef;
        internal MapRecordRef ToRef { get; } = toRef;
        internal Endpoint From { get; } = from;
        internal Endpoint To { get; } = to;
        internal string Relation { get; } = relation;
        internal bool Compiled { get; set; }
        internal MapExactValue? BottomY { get; set; }
        internal bool Overflow { get; set; }
        internal MapDocumentException? Error { get; set; }
    }
}
