using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Tuning for <see cref="TileRegionResidency"/>. Radii are in REGIONS at Chebyshev distance
/// (<c>max(|drx|, |drz|)</c>), so a radius of 1 is the 3x3 block around the observer's own region, which at the
/// 64-tile region size is a 192 m square at the default tile size.
/// <para><b>Why Chebyshev.</b> The observer sits anywhere inside its own region, corner included, so the only
/// guarantee worth stating is the distance to the nearest region that is NOT resident. A square ring gives
/// exactly <c>LoadRadius</c> regions of guaranteed coverage in every direction with no special case for the
/// diagonal, which a round ring does not (at radius 1 a round ring excludes the diagonal neighbour entirely and
/// the observer can stand right against it). This is the same reasoning, and the same metric, as
/// <c>MapResidencyConfig</c> in <c>KhaozEngine.MapDoc</c>.</para>
/// <para><see cref="UnloadRadius"/> is the hysteresis boundary and must exceed <see cref="LoadRadius"/> by at
/// least one region. Without that band an observer walking back and forth across a region border would load and
/// unload the same column on alternate frames, and a region load is a file read plus a full remesh.</para>
/// <para>A defaulted struct (<c>default</c>) is all zeroes, which is the degenerate band rather than these
/// defaults, and <see cref="TileRegionResidency"/> refuses it. Use <see cref="Default"/>.</para></summary>
/// <param name="LoadRadius">Regions around the observer's own that are kept loaded.</param>
/// <param name="UnloadRadius">Chebyshev distance past which a loaded region is dropped.</param>
/// <param name="MaxLoadsPerUpdate">Region loads one <see cref="TileRegionResidency.Update"/> may perform.</param>
public readonly record struct TileResidencyConfig(
    int LoadRadius = TileResidencyConfig.DefaultLoadRadius,
    int UnloadRadius = TileResidencyConfig.DefaultUnloadRadius,
    int MaxLoadsPerUpdate = TileResidencyConfig.DefaultMaxLoadsPerUpdate)
{
    /// <summary>The 3x3 block around the observer's region, the default <see cref="LoadRadius"/>.</summary>
    public const int DefaultLoadRadius = 1;

    /// <summary>One region of hysteresis past the load ring, the default <see cref="UnloadRadius"/>.</summary>
    public const int DefaultUnloadRadius = 2;

    /// <summary>Two loads an update, the default <see cref="MaxLoadsPerUpdate"/>.</summary>
    public const int DefaultMaxLoadsPerUpdate = 2;

    /// <summary>LoadRadius 1, UnloadRadius 2, 2 loads an update: a 3x3 ring of regions (192 m across at the
    /// default tile size), one region of hysteresis, and a budget that refills the five-region column a border
    /// crossing brings into range in three updates. Spelled out rather than <c>new()</c>, because a struct's
    /// parameterless form zeroes its fields instead of applying the parameter defaults.</summary>
    public static TileResidencyConfig Default =>
        new(DefaultLoadRadius, DefaultUnloadRadius, DefaultMaxLoadsPerUpdate);

    /// <summary>Throws when the ring is degenerate, blaming <paramref name="paramName"/> for it.</summary>
    /// <param name="paramName">The argument name to report, for a caller validating on someone's behalf.</param>
    /// <exception cref="ArgumentException">A radius is negative, the hysteresis band is not at least one region
    /// wide, or the per-update budget is not positive.</exception>
    public void Validate(string? paramName = null)
    {
        if (LoadRadius < 0)
            throw new ArgumentException($"LoadRadius ({LoadRadius}) must not be negative.", paramName);
        if (UnloadRadius < LoadRadius + 1)
            throw new ArgumentException(
                $"UnloadRadius ({UnloadRadius}) must exceed LoadRadius ({LoadRadius}) so the hysteresis band stops churn.",
                paramName);
        if (MaxLoadsPerUpdate < 1)
            throw new ArgumentException($"MaxLoadsPerUpdate ({MaxLoadsPerUpdate}) must be positive.", paramName);
    }
}

/// <summary>Keeps a square ring of REGIONS resident around one observer tile: it materialises each region
/// through a <see cref="TileWorldSource"/> and hands it to a <see cref="TileWorldView"/>, then drops both again
/// once the observer has moved past the hysteresis band. The whole streaming client in one type, and GPU-free by
/// construction, because everything the view does goes through its scene seam.
/// <para><b>Order matters on both sides.</b> A load is source first then view, because the view meshes what the
/// document holds and an empty region meshes to nothing. An unload is view first then source, because the view's
/// mesh handles have to be freed before the data they were built from goes.</para>
/// <para><b>Streaming a region dirties its neighbours.</b> A region mesh is not self-contained: the ground
/// mesher reads the far-edge corner heights, the central-difference normals and the four-tile corner colour
/// blend ACROSS the region border, and an absent neighbour edge-extends instead. So a region meshed while its
/// neighbour was absent carries a border built from the wrong data, and would keep it forever, which on a ridge
/// along the shared border is a full-height crack rather than a subtle seam. Every load and every unload
/// therefore marks the eight surrounding regions dirty on every plane. Marking wide is free, because the view's
/// flush drops a mark on a region that is not loaded, and the view's own per-flush budget is what keeps the
/// resulting burst off one frame.</para>
/// <para><b>A torn region file throws out of <see cref="Update"/>.</b>
/// <see cref="TileWorldSource.EnsureLoaded(RegionCoord)"/>
/// hash-checks the bytes and raises <see cref="TileWorldException"/> when they disagree with the manifest, and
/// this passes it straight through: a world whose files no longer match what wrote them is not something a
/// streaming loop should paper over by drawing a hole.</para>
/// <para><b>A dirty region is never dropped.</b> <see cref="TileWorldSource.Unload"/> throws on a region with
/// unsaved edits, so an editor that walks away from its own unsaved work would take an exception on a frame that
/// has nothing to do with editing. This keeps that region resident instead and says so once through
/// <see cref="Log"/>. It leaves the resident set able to exceed the ring, which is the correct trade: an editor
/// holds a handful of dirty regions at most, and the alternative is losing the edit.</para>
/// <para>Regions the manifest does not list are skipped entirely rather than treated as an error, and they do
/// not consume the per-update budget. The edge of an authored world is a normal place to stand: the view draws
/// nothing there and collision reads the tiles as blocked.</para></summary>
public sealed class TileRegionResidency
{
    readonly TileWorldSource _source;
    readonly TileWorldView _view;
    readonly TileResidencyConfig _config;

    // Regions already reported as dirty-and-kept, so an observer standing still logs one line rather than one an
    // update. A region drops out of here when it leaves for real, and also when it comes back INSIDE the unload
    // radius, so the promise is one line per spell outside the ring rather than one line ever: a region that is
    // saved, re-entered and dirtied again reports again, which is the behaviour an editor wants.
    readonly HashSet<RegionCoord> _dirtyReported = new();

    // Reused across updates: the loaded set as this update found it, this update's load candidates, and the
    // regions leaving. None of them outlive the call, and none of them are ever handed out.
    readonly HashSet<RegionCoord> _loaded = new();
    readonly List<Candidate> _candidates = new();
    readonly List<RegionCoord> _departing = new();

    readonly record struct Candidate(RegionCoord Region, int Distance);

    /// <summary>Binds a ring to a source and the view that draws it. Neither is owned: the source outlives the
    /// ring (it is the world) and the view is disposed by whoever built it.</summary>
    /// <param name="source">The world on disk regions are materialised from.</param>
    /// <param name="view">The view that meshes a region and frees it again.</param>
    /// <param name="config">The ring's tuning, validated here.</param>
    /// <exception cref="ArgumentException">The config is degenerate, see <see cref="TileResidencyConfig.Validate"/>.</exception>
    public TileRegionResidency(TileWorldSource source, TileWorldView view, TileResidencyConfig config)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(view);
        config.Validate(nameof(config));
        _source = source;
        _view = view;
        _config = config;
    }

    /// <summary>Where a region kept resident because it is dirty is reported, once per region. Null discards the
    /// line. Separate from the view's own log, so a host can route a streaming decision somewhere other than the
    /// renderer's diagnostics.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>The tuning this ring runs on.</summary>
    public TileResidencyConfig Config => _config;

    /// <summary>The resident regions, straight from the view, which is the single authority on what is loaded.
    /// A snapshot rather than a live view, so a caller may load or unload while walking it.</summary>
    public IReadOnlyCollection<RegionCoord> Resident => _view.LoadedRegions;

    /// <summary>Moves the ring to the observer's region: drops what has fallen past <see cref="TileResidencyConfig.UnloadRadius"/>,
    /// then loads up to <see cref="TileResidencyConfig.MaxLoadsPerUpdate"/> of what is missing inside
    /// <see cref="TileResidencyConfig.LoadRadius"/>, nearest first. Only the observer's (x, z) matters, because
    /// a region is a column: its planes stream together.
    /// <para>The budget is what keeps a frame's cost bounded, and it is also why this is not enough on its own
    /// after a teleport. A discontinuous move leaves the observer standing in a region that is several updates
    /// away from loading, so a jump runs <see cref="PrimeAround"/> instead.</para></summary>
    /// <param name="observer">The tile the ring is centred on.</param>
    public void Update(TileCoord observer) => Move(observer, _config.MaxLoadsPerUpdate);

    /// <summary>Fills the whole ring around the observer in one call, ignoring the per-update budget, and drops
    /// what the move left behind. The loading-moment form: a teleport, a zone change or a camera jump runs this
    /// before the next draw, so nothing renders a hole while the budget catches up.</summary>
    /// <param name="observer">The tile the ring is centred on.</param>
    public void PrimeAround(TileCoord observer) => Move(observer, int.MaxValue);

    void Move(TileCoord observer, int budget)
    {
        RegionCoord centre = RegionCoord.Of(observer.X, observer.Z);

        _loaded.Clear();
        foreach (RegionCoord c in _view.LoadedRegions) _loaded.Add(c);

        DropDeparted(centre);
        LoadArrivals(centre, budget);
    }

    void DropDeparted(RegionCoord centre)
    {
        _departing.Clear();
        foreach (RegionCoord c in _loaded)
        {
            if (Chebyshev(c, centre) > _config.UnloadRadius) _departing.Add(c);
            else _dirtyReported.Remove(c);
        }

        for (int i = 0; i < _departing.Count; i++)
        {
            RegionCoord c = _departing[i];
            if (_source.Document.GetRegion(c)?.Dirty == true)
            {
                if (_dirtyReported.Add(c))
                    Log?.Invoke($"tile world: region {c} has unsaved changes and is kept resident past the unload radius. Save the world to let it stream out.");
                continue;
            }
            _view.UnloadRegion(c);
            MarkNeighboursDirty(c);
            _source.Unload(c);
            _loaded.Remove(c);
            _dirtyReported.Remove(c);
        }
    }

    void LoadArrivals(RegionCoord centre, int budget)
    {
        _candidates.Clear();
        int r = _config.LoadRadius;
        for (int dz = -r; dz <= r; dz++)
            for (int dx = -r; dx <= r; dx++)
            {
                var c = new RegionCoord(centre.Rx + dx, centre.Rz + dz);
                if (_loaded.Contains(c)) continue;
                // The manifest, not the filesystem, decides what exists. Skipping here rather than at the load
                // is what keeps an unknown region off the budget, so an observer at the edge of the world still
                // fills the half of its ring that is authored.
                if (!_source.IsKnown(c)) continue;
                _candidates.Add(new Candidate(c, Math.Max(Math.Abs(dx), Math.Abs(dz))));
            }
        if (_candidates.Count == 0) return;
        _candidates.Sort(NearestFirst);

        int taken = 0;
        for (int i = 0; i < _candidates.Count && taken < budget; i++)
        {
            RegionCoord c = _candidates[i].Region;
            // Null is the manifest changing under us between the IsKnown check and here, which nothing in this
            // process does. Skipped rather than asserted, and it does not spend the budget.
            if (_source.EnsureLoaded(c) is null) continue;
            _view.LoadRegion(c);
            MarkNeighboursDirty(c);
            _loaded.Add(c);
            taken++;
        }
    }

    // Every region touching this one at a corner or an edge, on every plane. The region itself is left alone: a
    // load has just meshed it from complete data, and an unload has just dropped its handles.
    void MarkNeighboursDirty(RegionCoord c)
    {
        int planes = _source.Document.PlaneCount;
        for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dz == 0) continue;
                var neighbour = new RegionCoord(c.Rx + dx, c.Rz + dz);
                for (int plane = 0; plane < planes; plane++) _view.MarkDirty(neighbour, plane);
            }
    }

    // Nearest first, with an ascending (rz, then rx) tie-break so the whole ring has ONE order. Without it the
    // pair of regions that arrive on a budgeted update would depend on nothing in particular, and a streaming
    // bug would reproduce on one run in four.
    static int NearestFirst(Candidate a, Candidate b)
    {
        if (a.Distance != b.Distance) return a.Distance.CompareTo(b.Distance);
        if (a.Region.Rz != b.Region.Rz) return a.Region.Rz.CompareTo(b.Region.Rz);
        return a.Region.Rx.CompareTo(b.Region.Rx);
    }

    static int Chebyshev(RegionCoord a, RegionCoord b) =>
        Math.Max(Math.Abs(a.Rx - b.Rx), Math.Abs(a.Rz - b.Rz));
}
