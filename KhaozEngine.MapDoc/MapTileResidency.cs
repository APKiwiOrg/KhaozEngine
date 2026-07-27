using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapDoc;

/// <summary>Keeps a square ring of DOCUMENT TILES resident around one or more foci, reading each tile on demand
/// through a <see cref="MapDocumentSource"/> and handing arrivals and departures to an
/// <see cref="IMapTileSink"/>. GPU-free and driven from a position, so a client and a headless server run the
/// same type.
/// <para><b>It does not own chunks and a chunk streamer does not own tiles.</b> <see cref="MapTileResidency"/>
/// owns document tile lifetime, <see cref="TerrainStreamer"/> owns chunk lifetime, and the chunk sink reads
/// resident document data. Nothing is tracked twice. The composition contract is three lines:</para>
/// <list type="number">
/// <item><see cref="Update(Vector3)"/> runs BEFORE <c>TerrainStreamer.Update</c> in the same frame.</item>
/// <item><c>streamer.BuildGate = residency.GateFor(chunkSize, sculptCellSize)</c>, so a chunk whose document
/// data has not arrived is deferred rather than built against bare analytic terrain.</item>
/// <item><see cref="MapResidencyConfig.ValidateAgainst"/> runs at wiring time, against the widest render
/// profile.</item>
/// </list>
/// <para><b>The teleport contract.</b> Ordering residency first is necessary and NOT sufficient, because
/// residency is async by default: a discontinuous focus move leaves the streamer asking for chunks whose tiles
/// are many frames away, and those chunks build with no sculpt and no placements, which is a fall-through
/// hazard rather than a cosmetic pop. So every teleport, zone change and camera jump runs
/// <c>residency.PrimeAround(newFocus)</c> then <c>streamer.UnloadAll()</c>, both before the next
/// <c>streamer.Update</c>. Residency first, so the streamer's first ring at the new location builds against a
/// complete document set. This is the step most likely to be missed, because without it the world looks right
/// within a few frames and the failure only shows as a brief fall-through on arrival.</para>
/// <para><b>Threading.</b> Tile reads run on the dispatcher's worker thread (file read plus parse, no device, no
/// shared state). Every <see cref="IMapTileSink"/> callback fires on the calling thread inside
/// <see cref="Update(Vector3)"/> before it returns, so a consumer adds and frees physics bodies without a lock.
/// Last-request-wins and cancel-on-departure ride a per-tile generation token, the same invariant
/// <c>ChunkBuildScheduler</c> maintains for chunks. <see cref="PlacementsIn"/> and the
/// <see cref="GateFor">build gate</see> are the two members safe to call from a build thread: both read one
/// immutable published snapshot. <see cref="Resident"/>, <see cref="RingOf"/> and
/// <see cref="TryGetContent"/> are frame-thread members.</para></summary>
public sealed class MapTileResidency : IDisposable, IPlacementSource
{
    readonly MapDocumentSource _source;
    readonly MapResidencyConfig _config;
    readonly IMapTileSink _sink;
    readonly IChunkBuildDispatcher _dispatcher;
    readonly TerrainField? _field;
    readonly float _tileSize;

    // Frame-thread authority. _resident is the ring each resident tile is currently at, _content its parsed
    // content. _desired is this update's target set, kept as a field so the apply step reads the CURRENT ring.
    readonly Dictionary<MapTileCoord, ChunkRing> _resident = new();
    readonly Dictionary<MapTileCoord, MapTileContent> _content = new();
    readonly Dictionary<MapTileCoord, ChunkRing> _desired = new();

    // Async bookkeeping: one generation per request, so a superseded or cancelled read is dropped rather than
    // applied. _ready holds parsed-and-still-current tiles waiting for the per-update apply budget.
    readonly Dictionary<MapTileCoord, long> _inFlight = new();
    readonly Dictionary<MapTileCoord, MapTileContent> _ready = new();
    readonly ConcurrentQueue<Completion> _done = new();
    long _nextGen = 1;

    // Published immutable snapshot, the ONLY state a build thread reads. Volatile publication is what makes a
    // reader see one whole consistent generation instead of a dictionary mid-mutation.
    volatile Dictionary<MapTileCoord, MapTileContent> _snapshot = new();

    // This update's foci, copied out of the caller's span so the ordering helpers can read them after it is
    // gone. Reused every update, never reallocated.
    readonly List<Vector3> _foci = new();
    readonly List<Pending> _pending = new();
    readonly List<Pending> _applyOrder = new();
    readonly List<MapTileCoord> _scratch = new();
    bool _disposed;

    readonly record struct Completion(MapTileCoord Coord, long Gen, MapTileContent? Content, Exception? Error);

    readonly record struct Pending(MapTileCoord Coord, float Dist);

    /// <summary>Builds residency over a source and a sink. <paramref name="dispatcher"/> chooses how tile reads
    /// run when <see cref="MapResidencyConfig.Async"/> is set (null uses the thread pool). Tests inject a manual
    /// dispatcher to control completion order, and it is ignored in synchronous mode.
    /// <para><paramref name="field"/> is needed ONLY by the <see cref="IPlacementSource"/> path, and only for
    /// authored placements that omit Y: those ground-snap to the field, deterministically, exactly as
    /// <c>MapRuntime.BuildPlacements</c> does. Leave it null when nothing queries placements through this type,
    /// or when every authored placement carries an explicit Y. Querying a null-Y placement with no field throws
    /// rather than inventing a height.</para></summary>
    /// <exception cref="ArgumentException">The hysteresis band is degenerate
    /// (<see cref="MapResidencyConfig.UnloadRadius"/> must exceed the outer load radius), a radius is negative,
    /// or the budget is not positive.</exception>
    public MapTileResidency(MapDocumentSource source, MapResidencyConfig config, IMapTileSink sink,
                            IChunkBuildDispatcher? dispatcher = null, TerrainField? field = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        if (config.LoadRadius < 0 || config.DecorRadius < 0)
            throw new ArgumentException($"LoadRadius ({config.LoadRadius}) and DecorRadius ({config.DecorRadius}) must not be negative.", nameof(config));
        if (config.UnloadRadius <= config.OuterRadius)
            throw new ArgumentException(
                $"UnloadRadius ({config.UnloadRadius}) must exceed the outer load radius ({config.OuterRadius}) so the hysteresis band stops churn.",
                nameof(config));
        if (config.MaxLoadsPerUpdate <= 0)
            throw new ArgumentException($"MaxLoadsPerUpdate ({config.MaxLoadsPerUpdate}) must be positive.", nameof(config));
        _config = config;
        _dispatcher = dispatcher ?? new TaskChunkBuildDispatcher();
        _field = field;
        _tileSize = source.Tiles.TileSize;
    }

    /// <summary>The source tiles are read from. Its <c>Tiles</c> index is the sole authority on which tiles
    /// exist, which is what lets residency skip an absent tile without touching the filesystem.</summary>
    public MapDocumentSource Source => _source;

    /// <summary>The document tile edge in metres, from the source's manifest.</summary>
    public float TileSize => _tileSize;

    /// <summary>The tuning this residency runs on.</summary>
    public MapResidencyConfig Config => _config;

    /// <summary>The resident set, UNORDERED and deliberately so: it changes every update, sorting it per update
    /// would be pure cost, and unlike <c>MapSpatialIndex.OccupiedTiles</c> it is never a hash input. A caller
    /// that needs a deterministic sequence sorts it itself.</summary>
    public IReadOnlyCollection<MapTileCoord> Resident => _resident.Keys;

    /// <summary>The ring a resident tile is at, or null when it is not resident. A <see cref="ChunkRing.Decor"/>
    /// tile is fully LOADED - the ring governs what a consumer BUILDS from a tile, never whether its data is
    /// present, which is exactly what lets the data rule use the outer radius and the collider rule use the load
    /// radius.</summary>
    public ChunkRing? RingOf(MapTileCoord coord) => _resident.TryGetValue(coord, out ChunkRing r) ? r : null;

    /// <summary>The parsed content of a resident tile.</summary>
    public bool TryGetContent(MapTileCoord coord, out MapTileContent content)
    {
        bool ok = _content.TryGetValue(coord, out MapTileContent? c);
        content = c!;
        return ok;
    }

    /// <summary>True when the tile is resident. Reads the published snapshot, so it is safe from a build
    /// thread.</summary>
    internal bool IsResident(MapTileCoord coord) => _snapshot.ContainsKey(coord);

    /// <summary>Client form: one focus.</summary>
    public void Update(Vector3 focus)
    {
        ThrowIfDisposed();
        _foci.Clear();
        _foci.Add(focus);
        UpdateCore();
    }

    /// <summary>Server form: the union of the rings around every focus. A tile leaves residency only when NO
    /// focus keeps it, and a tile that is Gameplay for one focus and Decor for another resolves to the strongest
    /// ring any focus assigns it (the numerically lowest <see cref="ChunkRing"/>, which is
    /// <see cref="ChunkRing.Gameplay"/>). Strongest-wins is what makes the answer a pure function of the focus
    /// SET rather than its order: without it a tile flaps between rings from update to update and a consumer
    /// sheds and re-adds its colliders every frame. Nothing is reference counted - the set is recomputed from
    /// the foci each update.
    /// <para>Cost is O(foci * ring area): at 100 players and the default 5x5 ring that is 2,500 coordinate tests.
    /// Past a few hundred foci a shard server should drive one residency per cell rather than one global
    /// residency with a thousand foci (see the package README, and note the two known holes in that guidance,
    /// engine issue #341).</para></summary>
    public void Update(ReadOnlySpan<Vector3> foci)
    {
        ThrowIfDisposed();
        _foci.Clear();
        for (int i = 0; i < foci.Length; i++) _foci.Add(foci[i]);
        UpdateCore();
    }

    void UpdateCore()
    {
        ComputeDesired();

        bool changed = DropDeparted();
        changed |= ApplyRingChanges();
        RequestArrivals();

        Pump();
        changed |= ApplyReady(_config.MaxLoadsPerUpdate);
        if (changed) Publish();
    }

    /// <summary>Deterministic BLOCKING fill of the whole ring around one focus, for a loading moment rather than
    /// a frame: every tile in range is read and applied before this returns, ignoring the per-update budget.
    /// Half of the teleport contract on the class remarks - the other half is <c>streamer.UnloadAll()</c>
    /// straight after, so no chunk built for the old location lingers or lands late.</summary>
    public void PrimeAround(Vector3 focus)
    {
        ThrowIfDisposed();
        int before = -1;
        while (_resident.Count != before)
        {
            before = _resident.Count;
            Update(focus);
            FlushPendingLoads();
        }
    }

    /// <summary>Force every outstanding tile read to finish and apply all of them now, ignoring the per-update
    /// budget. Turns the async layer into a blocking load for this call. A no-op in synchronous mode, where
    /// reads are already applied inline.</summary>
    public void FlushPendingLoads()
    {
        ThrowIfDisposed();
        _dispatcher.Drain();
        Pump();
        if (ApplyReady(int.MaxValue)) Publish();
    }

    /// <summary>The build gate to hand <see cref="TerrainStreamer.BuildGate"/>, for a given chunk size and sculpt
    /// cell size. A chunk is buildable only when every document tile its sculpt-expanded footprint touches is
    /// either resident or unoccupied. See <see cref="IChunkBuildGate"/>.</summary>
    public IChunkBuildGate GateFor(float chunkSize, float sculptCellSize) =>
        new MapResidencyGate(this, chunkSize, sculptCellSize);

    /// <summary>Re-read one resident tile from the source (an editor wrote it, a tool regenerated it). Fires
    /// <see cref="IMapTileSink.TileUnloaded"/> then <see cref="IMapTileSink.TileLoaded"/> for it, because the
    /// bodies and sculpt a consumer built from the OLD content have to go before the new content replaces it.
    /// A no-op for a tile that is not resident: it picks up the new content when it next arrives.</summary>
    /// <exception cref="MapDocumentException">The tile cannot be read or fails per-tile validation.</exception>
    public void Invalidate(MapTileCoord coord)
    {
        ThrowIfDisposed();
        if (!_resident.TryGetValue(coord, out ChunkRing ring)) return;
        MapTileContent content = _source.ReadTile(coord);
        _sink.TileUnloaded(coord);
        _content[coord] = content;
        _sink.TileLoaded(coord, content, ring);
        Publish();
    }

    /// <summary>Drop every resident tile through the sink and discard any outstanding read. After this
    /// <see cref="Resident"/> is empty. Departures fire in ascending (Z, then X) order so a teardown is
    /// reproducible.</summary>
    public void UnloadAll()
    {
        Reset();
        if (_resident.Count == 0) return;
        _scratch.Clear();
        foreach (MapTileCoord c in _resident.Keys) _scratch.Add(c);
        _scratch.Sort(static (a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : a.X.CompareTo(b.X));
        _resident.Clear();
        _content.Clear();
        foreach (MapTileCoord c in _scratch) _sink.TileUnloaded(c);
        Publish();
    }

    /// <summary>Unload everything and stop. Does NOT dispose the <see cref="MapDocumentSource"/>: a source is
    /// commonly shared with the rest of the game (the manifest is the world's globals), so residency borrows it
    /// rather than owning it. Idempotent.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        UnloadAll();
        _disposed = true;
    }

    // --- IPlacementSource ----------------------------------------------------------------------------------

    /// <summary>The authored placements of every RESIDENT tile whose (X, Z) falls in the half-open rect,
    /// appended to the caller's list as engine <see cref="PropPlacement"/>s. Called on the chunk build thread:
    /// it takes ONE read of the published snapshot, so a build always sees one whole consistent generation of
    /// the resident set even while <see cref="Update(Vector3)"/> is mutating it on the frame thread.
    /// <para>This is what carries a streamed tile's props to the renderer at all: a frozen
    /// <c>PropLayer.Placements</c> list is bucketed once at sink construction, so a placement arriving later
    /// would never draw. Wire it as <c>PropLayer.PlacementLayer(residency, meshes, drawRadius)</c> and the
    /// consumer writes no glue.</para></summary>
    /// <exception cref="InvalidOperationException">A placement in range omits Y and this residency was built
    /// with no <c>TerrainField</c> to ground-snap against.</exception>
    public void PlacementsIn(RectArea area, List<PropPlacement> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        Dictionary<MapTileCoord, MapTileContent> snapshot = _snapshot;   // one read, one generation
        if (snapshot.Count == 0) return;

        MapTileRect range = MapTileGrid.RectOf(area, _tileSize);
        for (int z = range.Min.Z; z <= range.Max.Z; z++)
        for (int x = range.Min.X; x <= range.Max.X; x++)
        {
            if (!snapshot.TryGetValue(new MapTileCoord(x, z), out MapTileContent? content)) continue;
            IReadOnlyList<MapPlacement> placements = content.Placements;
            for (int i = 0; i < placements.Count; i++)
            {
                MapPlacement p = placements[i];
                if (!MapSpatialIndex.InArea(p.X, p.Z, area)) continue;
                float y = p.Y ?? Snap(p);
                into.Add(new PropPlacement(p.Kind, p.X, y, p.Z, p.Scale, p.Yaw, 0));
            }
        }
    }

    float Snap(MapPlacement p) =>
        _field?.SampleHeight(p.X, p.Z)
        ?? throw new InvalidOperationException(
            $"placement '{p.Id}' has no Y and this MapTileResidency was built without a TerrainField to ground-snap against. " +
            "Pass one to the constructor when serving placements through IPlacementSource.");

    // --- Update steps --------------------------------------------------------------------------------------

    void ComputeDesired()
    {
        _desired.Clear();
        int r = _config.OuterRadius;
        MapTileIndex tiles = _source.Tiles;
        for (int f = 0; f < _foci.Count; f++)
        {
            MapTileCoord fc = MapTileGrid.CoordOf(_foci[f].X, _foci[f].Z, _tileSize);
            for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                int cheb = Math.Max(Math.Abs(dx), Math.Abs(dz));
                var c = new MapTileCoord(fc.X + dx, fc.Z + dz);
                // Absent tiles are skipped here rather than read and found empty: in a sparse world most of the
                // ring holds no authored content and has no file, so consulting the manifest index is what keeps
                // residency off the filesystem entirely for them.
                if (!tiles.IsOccupied(c)) continue;
                ChunkRing ring = cheb <= _config.LoadRadius ? ChunkRing.Gameplay : ChunkRing.Decor;
                // Strongest wins, and min is order-independent by construction.
                if (_desired.TryGetValue(c, out ChunkRing existing) && existing <= ring) continue;
                _desired[c] = ring;
            }
        }
    }

    bool DropDeparted()
    {
        int unload = _config.UnloadRadius;
        bool changed = false;

        if (_resident.Count > 0)
        {
            _scratch.Clear();
            foreach (MapTileCoord c in _resident.Keys)
                if (MinChebyshev(c) > unload) _scratch.Add(c);
            for (int i = 0; i < _scratch.Count; i++)
            {
                MapTileCoord c = _scratch[i];
                _resident.Remove(c);
                _content.Remove(c);
                _sink.TileUnloaded(c);
                changed = true;
            }
        }

        // Cancel reads for tiles that left before they landed. The running body still finishes on its worker
        // thread, and its result is dropped at Pump because the generation no longer matches.
        if (_inFlight.Count > 0)
        {
            _scratch.Clear();
            foreach (MapTileCoord c in _inFlight.Keys)
                if (MinChebyshev(c) > unload) _scratch.Add(c);
            for (int i = 0; i < _scratch.Count; i++) _inFlight.Remove(_scratch[i]);
        }

        if (_ready.Count > 0)
        {
            _scratch.Clear();
            foreach (MapTileCoord c in _ready.Keys)
                if (MinChebyshev(c) > unload) _scratch.Add(c);
            for (int i = 0; i < _scratch.Count; i++) _ready.Remove(_scratch[i]);
        }

        return changed;
    }

    bool ApplyRingChanges()
    {
        bool changed = false;
        _scratch.Clear();
        foreach (KeyValuePair<MapTileCoord, ChunkRing> kv in _resident)
            // A tile inside the hysteresis band but outside the load ring is absent from _desired and KEEPS its
            // ring: the band is what stops a boundary-straddling focus from churning.
            if (_desired.TryGetValue(kv.Key, out ChunkRing want) && want != kv.Value) _scratch.Add(kv.Key);
        for (int i = 0; i < _scratch.Count; i++)
        {
            MapTileCoord c = _scratch[i];
            ChunkRing ring = _desired[c];
            _resident[c] = ring;
            _sink.TileRingChanged(c, _content[c], ring);
            changed = true;
        }
        return changed;
    }

    void RequestArrivals()
    {
        _pending.Clear();
        foreach (KeyValuePair<MapTileCoord, ChunkRing> kv in _desired)
        {
            MapTileCoord c = kv.Key;
            if (_resident.ContainsKey(c) || _inFlight.ContainsKey(c) || _ready.ContainsKey(c)) continue;
            _pending.Add(new Pending(c, MinDistance(c)));
        }
        if (_pending.Count == 0) return;
        _pending.Sort(NearestFirst);

        if (_config.Async)
        {
            // Unbudgeted: the read runs off the frame thread, and the budget lands on the apply step where the
            // consumer's own per-tile work (physics bodies, sculpt rebuild) actually costs a frame.
            for (int i = 0; i < _pending.Count; i++) Request(_pending[i].Coord);
            return;
        }

        // Synchronous: the read IS the frame cost, so the budget caps reads instead.
        int budget = Math.Min(_config.MaxLoadsPerUpdate, _pending.Count);
        for (int i = 0; i < budget; i++)
        {
            MapTileCoord c = _pending[i].Coord;
            _ready[c] = _source.ReadTile(c);
        }
    }

    void Request(MapTileCoord coord)
    {
        long gen = _nextGen++;
        _inFlight[coord] = gen;
        MapDocumentSource source = _source;
        ConcurrentQueue<Completion> done = _done;
        _dispatcher.Schedule(() =>
        {
            MapTileContent? content = null;
            Exception? error = null;
            try { content = source.ReadTile(coord); }
            catch (Exception e) { error = e; }
            done.Enqueue(new Completion(coord, gen, content, error));
        });
    }

    void Pump()
    {
        while (_done.TryDequeue(out Completion c))
        {
            if (!_inFlight.TryGetValue(c.Coord, out long gen) || gen != c.Gen)
                continue;   // cancelled or superseded: drop the parsed tile
            _inFlight.Remove(c.Coord);
            if (c.Error is not null)
                throw new MapDocumentException(
                    $"reading document tile ({c.Coord.X}, {c.Coord.Z}) failed: {c.Error.Message}", c.Error);
            _ready[c.Coord] = c.Content!;
        }
    }

    bool ApplyReady(int max)
    {
        if (_ready.Count == 0 || max <= 0) return false;
        _applyOrder.Clear();
        foreach (MapTileCoord c in _ready.Keys) _applyOrder.Add(new Pending(c, MinDistance(c)));
        _applyOrder.Sort(NearestFirst);
        int take = Math.Min(max, _applyOrder.Count);
        for (int i = 0; i < take; i++)
        {
            MapTileCoord c = _applyOrder[i].Coord;
            MapTileContent content = _ready[c];
            _ready.Remove(c);
            ChunkRing ring = _desired.TryGetValue(c, out ChunkRing r) ? r : ChunkRing.Gameplay;
            _resident[c] = ring;
            _content[c] = content;
            _sink.TileLoaded(c, content, ring);
        }
        return take > 0;
    }

    // Nearest first, with an ascending (Z, then X) tie-break so two tiles equidistant from the focus still have
    // ONE order. Without the tie-break the apply order at a budget boundary would depend on dictionary iteration
    // order, and which tile arrived this update rather than next would drift between runs.
    static int NearestFirst(Pending a, Pending b)
    {
        int cmp = a.Dist.CompareTo(b.Dist);
        if (cmp != 0) return cmp;
        return a.Coord.Z != b.Coord.Z ? a.Coord.Z.CompareTo(b.Coord.Z) : a.Coord.X.CompareTo(b.Coord.X);
    }

    void Publish() => _snapshot = new Dictionary<MapTileCoord, MapTileContent>(_content);

    void Reset()
    {
        _dispatcher.Drain();
        while (_done.TryDequeue(out _)) { }
        _inFlight.Clear();
        _ready.Clear();
    }

    void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Chebyshev tile distance to the NEAREST focus, which is the metric the whole ring is drawn in.
    /// int.MaxValue with no foci at all, so an Update with an empty span unloads everything rather than keeping
    /// a stale ring.</summary>
    int MinChebyshev(MapTileCoord c)
    {
        int best = int.MaxValue;
        for (int i = 0; i < _foci.Count; i++)
        {
            MapTileCoord fc = MapTileGrid.CoordOf(_foci[i].X, _foci[i].Z, _tileSize);
            int d = Math.Max(Math.Abs(c.X - fc.X), Math.Abs(c.Z - fc.Z));
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Squared world distance from the tile's centre to the nearest focus. Squared is enough: it only
    /// ever orders, never measures.</summary>
    float MinDistance(MapTileCoord c)
    {
        Vector2 centre = MapTileGrid.CenterOf(c, _tileSize);
        float best = float.MaxValue;
        for (int i = 0; i < _foci.Count; i++)
        {
            float dx = centre.X - _foci[i].X, dz = centre.Y - _foci[i].Z;
            float d = dx * dx + dz * dz;
            if (d < best) best = d;
        }
        return best;
    }
}
