using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Holds the one open document. All members lock internally.</summary>
/// <remarks>The single stateful object behind the ke-mapedit MCP server: the current document, its path, the
/// manifest paths, a shared default registry, a dirty flag, and a cached <see cref="TerrainField"/> rebuilt
/// after world-affecting mutations. The in-session document is kept valid by convention (mutations validate at
/// a higher layer). This core exposes the primitives those higher layers build on.</remarks>
public sealed class MapEditSession
{
    readonly object _lock = new();
    readonly MapDocRegistry _registry = MapDocRegistry.CreateDefault();

    MapDocument? _doc;
    string? _path;
    IReadOnlyList<string> _manifests = Array.Empty<string>();
    bool _dirty;
    TerrainField? _field;
    MapTileRect? _window;

    /// <summary>Occupied-tile ceiling below which <see cref="Open"/> loads a tiled document whole, mirroring
    /// <c>MapEditorOptions.WholeWorldTileLimit</c> so the GUI editor and this MCP session agree on when a
    /// world is too large to load whole. Settable so a test can exercise the windowed path against a small
    /// synthetic world instead of a real 512-tile one.</summary>
    public int WholeWorldTileLimit { get; set; } = MapDocumentWindowing.DefaultWholeWorldTileLimit;

    /// <summary>Tile radius either side of the window center when <see cref="Open"/> windows a large document.</summary>
    public int EditorWindowRadius { get; set; } = MapDocumentWindowing.DefaultEditorWindowRadius;

    /// <summary>Loads the document at <paramref name="path"/>, replacing any open document. A monolithic file
    /// or a tiled directory at or under <see cref="WholeWorldTileLimit"/> occupied tiles loads whole. A larger
    /// tiled directory opens windowed (<see cref="MapDocumentWindowing"/>), same rule the GUI editor uses.
    /// There is no dirty guard: the client's git diff is the safety net, but <see cref="MapSummary.Dirty"/>
    /// reports unsaved state. Throws <see cref="MapDocumentException"/> (naming the path) on any load failure.</summary>
    public OpenResult Open(string path, IReadOnlyList<string>? manifestPaths = null)
    {
        lock (_lock)
        {
            var options = new MapDocumentLoadOptions { Registry = _registry };
            MapDocument doc = MapDocumentWindowing.Load(path, options, WholeWorldTileLimit, EditorWindowRadius,
                out _, out MapTileRect? window);
            _doc = doc;
            _path = path;
            _manifests = CopyManifests(manifestPaths);
            _dirty = false;
            _field = null;
            _window = window;
            return new OpenResult(path, doc.Id, doc.DisplayName, BuildSummaryLocked());
        }
    }

    /// <summary>Creates a fresh document with one default all-open Meadow biome band (so scatter rules have a
    /// biome to bind to), validates and saves it (monolithic: this is always a brand new document, so there is
    /// no existing form to preserve), and keeps it open. Creates parent directories. Throws
    /// <see cref="IOException"/> when something already exists at the path (a file OR a tiled directory) and
    /// <paramref name="overwrite"/> is false. <paramref name="overwrite"/> only ever replaces a monolithic
    /// FILE (this always writes monolithic): an existing tiled directory is refused even with
    /// <paramref name="overwrite"/> true, so the raw <see cref="FileStream"/> failure that opening a directory
    /// as a file would throw never surfaces.</summary>
    /// <exception cref="MapDocumentException">A tiled document (a directory) already exists at
    /// <paramref name="path"/>.</exception>
    public OpenResult Create(string path, string id, string displayName,
        float minX, float minZ, float maxX, float maxZ,
        int seed = 1, float waterLevel = 0f, bool overwrite = false,
        IReadOnlyList<string>? manifestPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            MapDocumentForm existingForm = MapDocumentFile.DetectForm(path);
            if (existingForm != MapDocumentForm.None && !overwrite)
                throw new IOException($"{path}: already exists. Pass overwrite to replace it.");
            if (existingForm == MapDocumentForm.Tiled)
                throw new MapDocumentException(
                    $"{path}: a tiled document (a directory) already exists there. Create always writes a " +
                    "monolithic file, so overwrite cannot replace a directory. Delete it first or choose a " +
                    "different path.");

            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            var doc = new MapDocument
            {
                Id = id,
                DisplayName = displayName,
                Bounds = new MapBounds { MinX = minX, MinZ = minZ, MaxX = maxX, MaxZ = maxZ },
                Terrain =
                {
                    Seed = seed,
                    WaterLevel = waterLevel,
                    Biomes = { new MapBiomeBand() },
                },
            };

            // Save validates first and throws on an invalid document, so a bad bounds/id fails loudly here.
            MapDocumentFile.Save(doc, path, _registry);

            _doc = doc;
            _path = path;
            _manifests = CopyManifests(manifestPaths);
            _dirty = false;
            _field = null;
            _window = null;
            return new OpenResult(path, doc.Id, doc.DisplayName, BuildSummaryLocked());
        }
    }

    /// <summary>Saves the open document back to its path, in the form it was opened or last converted into
    /// (<see cref="MapDocumentFile.SaveAuto"/>): a tiled directory saves tiled, a monolithic file saves
    /// monolithic, never converting implicitly. Validates first, throwing on invalid, and clears dirty on
    /// success.</summary>
    public SaveResult Save()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            if (MapDocumentFile.DetectForm(_path!) == MapDocumentForm.None)
                MapDocumentFile.Save(_doc!, _path!, _registry);
            else
                MapDocumentFile.SaveAuto(_doc!, _path!, _registry);
            _dirty = false;
            return new SaveResult(_path!, true);
        }
    }

    /// <summary>Moves the loaded window of the open TILED document, discarding whatever this session held
    /// before and reloading the manifest plus only the tiles inside the new window (world coordinates, the
    /// same rect a query verb like <c>sculpt_flatten_region</c> takes). With unsaved changes and
    /// <paramref name="discard"/> false this throws rather than losing them. Pass <paramref name="discard"/>
    /// true to move anyway. This session keeps no undo stack of its own (each mutation's <c>EditorCommand</c>
    /// is applied, validated, and discarded within one call, per <see cref="Mutate{T}"/>, never retained across
    /// calls the way the GUI editor's history is), so a window move has nothing to replay: the cached field and
    /// the dirty flag are what actually need resetting, and this does both.</summary>
    /// <exception cref="InvalidOperationException">No document is open, the open document is not a tiled
    /// directory, or it is dirty and <paramref name="discard"/> is false.</exception>
    public WindowStatusResult SetWindow(float minX, float minZ, float maxX, float maxZ, bool discard = false)
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            if (_doc!.Tiles is not { SourceDirectory: { } directory })
                throw new InvalidOperationException(
                    "set_window only applies to a tiled document opened from (or converted to) a directory. " +
                    "This document is monolithic or was built in memory.");
            if (_dirty && !discard)
                throw new InvalidOperationException(
                    "the open document has unsaved changes. Save first (map_save), then move the window, or " +
                    "pass discard to move it now and lose them.");

            var options = new MapDocumentLoadOptions { Registry = _registry };
            var rect = MapTileGrid.RectOf(new RectArea(minX, minZ, maxX, maxZ), _doc.TileSize);
            _doc = MapDocumentFile.LoadTiled(directory, rect, options);
            _path = directory;
            _window = rect;
            _dirty = false;
            _field = null;
            return BuildWindowStatusLocked();
        }
    }

    /// <summary>Reports the loaded window of the open document: the tile rect (null when the whole world is
    /// loaded, including a whole-loaded tiled document), and how many of the document's occupied tiles are
    /// currently loaded. Never throws for a monolithic or in-memory document, it just reports
    /// <see cref="WindowStatusResult.Tiled"/> false.</summary>
    public WindowStatusResult WindowStatus()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            return BuildWindowStatusLocked();
        }
    }

    /// <summary>Converts the open document to the TILED form at <paramref name="directory"/> via
    /// <see cref="MapDocumentFile.SaveAs"/> (explicit form, no extension heuristics), preserving
    /// <see cref="MapDocument.TileSize"/> exactly (world identity, <see cref="MapDocumentHash.OfWorld"/>, is
    /// unchanged by a form conversion). A windowed (partial) document is refused by <c>SaveTiled</c>'s own
    /// guard when <paramref name="directory"/> differs from the window's source directory, which it always
    /// does here (a conversion always targets a fresh location), so that refusal is inherited rather than
    /// re-implemented. A directory that already holds a tiled document is refused here, the same as
    /// <see cref="ConvertToSingle"/>: there is no overwrite parameter because a conversion targets a fresh
    /// location, never an existing world (unrefused, this silently replaced the target world's tiles and
    /// swept the rest away).</summary>
    /// <exception cref="MapDocumentException">A tiled document already exists at <paramref name="directory"/>.</exception>
    public ConvertResult ConvertToTiled(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (_lock)
        {
            RequireDocumentLocked();
            if (MapDocumentFile.DetectForm(directory) == MapDocumentForm.Tiled)
                throw new MapDocumentException(
                    $"{directory}: a tiled document already exists there. Convert or delete it first.");
            MapDocumentFile.SaveAs(_doc!, directory, MapDocumentForm.Tiled, _registry);
            _path = directory;
            _dirty = false;
            _field = null;
            _window = null;   // SaveTiled just refreshed doc.Tiles as a whole (unwindowed) index.
            return new ConvertResult(directory, nameof(MapDocumentForm.Tiled), _doc!.TileSize,
                MapDocumentHash.OfWorld(_doc, _registry));
        }
    }

    /// <summary>Converts the open document to the MONOLITHIC form at <paramref name="path"/> via
    /// <see cref="MapDocumentFile.SaveAs"/> (explicit form, no extension heuristics: <c>Path.GetExtension</c>
    /// on a path like <c>island.map</c> returns <c>".map"</c>, not empty, so an extension guess would route a
    /// directory-shaped name to the wrong writer). A windowed (partial) document is refused by
    /// <see cref="MapDocumentFile.Save"/>'s own guard unconditionally, inherited rather than re-implemented.
    /// <see cref="MapDocument.TileSize"/> is preserved exactly.</summary>
    public ConvertResult ConvertToSingle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            RequireDocumentLocked();
            if (MapDocumentFile.DetectForm(path) == MapDocumentForm.Tiled)
                throw new MapDocumentException(
                    $"{path}: a tiled document (a directory) already exists there. Convert or delete it first.");
            MapDocumentFile.SaveAs(_doc!, path, MapDocumentForm.Monolithic, _registry);
            _doc!.Tiles = null;   // now genuinely monolithic, matching what a fresh MapDocumentFile.Load gives.
            _path = path;
            _dirty = false;
            _field = null;
            _window = null;
            return new ConvertResult(path, nameof(MapDocumentForm.Monolithic), _doc.TileSize,
                MapDocumentHash.OfWorld(_doc, _registry));
        }
    }

    /// <summary>Sets <see cref="MapDocument.TileSize"/> and re-saves the open document at its own path (whatever
    /// form it is currently in). <c>tileSize</c> IS part of world identity
    /// (<see cref="MapDocumentHash.OfWorld"/>), so retiling a world (a pure storage decision that moves no
    /// content) still changes the world hash and needs a coordinated client and server release, which
    /// <see cref="RetileResult.Warning"/> states plainly with the before/after digests rather than leaving a
    /// caller to notice on its own.</summary>
    /// <exception cref="InvalidOperationException">No document is open.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="tileSize"/> is not positive and finite.</exception>
    /// <exception cref="MapDocumentException">The open document is windowed (partial): retiling rewrites every
    /// tile, which a partial document cannot do without silently dropping every tile the window did not
    /// load.</exception>
    public RetileResult Retile(float tileSize)
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            if (!(tileSize > 0f) || !float.IsFinite(tileSize))
                throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "tileSize must be positive and finite.");
            if (_doc!.Tiles is { IsPartial: true })
                throw new MapDocumentException(
                    "refusing to retile a windowed document: retiling rewrites every tile, and a partial " +
                    "document would silently drop every tile the window did not load. Load the whole world " +
                    "first (set_window over the full extent, or reopen without one).");

            string oldHash = MapDocumentHash.OfWorld(_doc, _registry);
            float oldTileSize = _doc.TileSize;
            _doc.TileSize = tileSize;
            try
            {
                MapDocumentFile.SaveAuto(_doc, _path!, _registry);
            }
            catch
            {
                // A rejected save must not leave the session holding an in-memory tileSize that was never
                // written: restore it, so the document (and IsDirty) still describe what is actually on disk.
                _doc.TileSize = oldTileSize;
                throw;
            }
            _dirty = false;
            _field = null;
            _window = null;
            string newHash = MapDocumentHash.OfWorld(_doc, _registry);
            string warning = string.Equals(oldHash, newHash, StringComparison.Ordinal)
                ? "tileSize is unchanged, so the world hash is unchanged."
                : $"tileSize is part of world identity: the world hash changed from {oldHash} to {newHash}. " +
                  "A client and server must ship this together, the same as any other world-hash change.";
            return new RetileResult(_path!, tileSize, oldHash, newHash, warning);
        }
    }

    /// <summary>Validates the open document. Structural = <see cref="MapDocumentValidator"/>. When structural
    /// passes, schema = <see cref="JsonSchemaValidator"/> over the serialized document against the packaged
    /// schema. When structural fails the schema check is skipped (serialization would throw) and its errors
    /// carry a note. A windowed (partial) document skips the schema check too:
    /// <see cref="MapDocumentFile.SaveText"/> (what the schema check serializes) refuses a partial document by
    /// the same guard every whole-document writer shares, so attempting it here would throw on every windowed
    /// session rather than degrade gracefully. Widen the window to validate the whole world's schema. Both
    /// skip paths return <see cref="ValidateResult.SchemaChecked"/> false alongside a false
    /// <see cref="ValidateResult.SchemaValid"/>, so a caller reading only <c>SchemaValid</c> cannot mistake
    /// "not checked" for "checked and invalid".</summary>
    public ValidateResult Validate()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            IReadOnlyList<string> structuralErrors = MapDocumentValidator.Validate(_doc!, _registry);
            bool structuralValid = structuralErrors.Count == 0;
            if (!structuralValid)
            {
                return new ValidateResult(false, structuralErrors, SchemaChecked: false, SchemaValid: false,
                    new[] { "schema check skipped because the document is structurally invalid." });
            }

            if (_doc!.Tiles is { IsPartial: true })
            {
                return new ValidateResult(true, Array.Empty<string>(), SchemaChecked: false, SchemaValid: false,
                    new[] { "schema check skipped: this is a windowed (partial) document. Widen the window " +
                            "(set_window over the full extent) to run the whole-document schema check." });
            }

            ValidationReport report = JsonSchemaValidator.Validate(
                MapDocumentFile.SaveText(_doc!, _registry), MapDocumentSchema.GetJson());
            return new ValidateResult(true, Array.Empty<string>(), SchemaChecked: true, report.IsValid, report.Errors);
        }
    }

    /// <summary>A flat summary of the open document (counts, names in fold order, dirty flag).</summary>
    public MapSummary Summary()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            return BuildSummaryLocked();
        }
    }

    /// <summary>Runs fn on the open document under the session lock. Throws if none open.</summary>
    public T WithDocument<T>(Func<MapDocument, MapDocRegistry, T> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        lock (_lock)
        {
            RequireDocumentLocked();
            return fn(_doc!, _registry);
        }
    }

    /// <summary>Mutation entry: runs fn under the lock, marks dirty, invalidates the cached field when
    /// worldChanged, always leaving validation to the caller-provided fn.</summary>
    public T Mutate<T>(Func<MapDocument, MapDocRegistry, T> fn, bool worldChanged)
    {
        ArgumentNullException.ThrowIfNull(fn);
        lock (_lock)
        {
            RequireDocumentLocked();
            T result = fn(_doc!, _registry);
            _dirty = true;
            if (worldChanged) _field = null;
            return result;
        }
    }

    /// <summary>The terrain field for the open document, lazily built and cached until a world-affecting
    /// mutation (<see cref="Mutate{T}"/> with worldChanged) invalidates it.</summary>
    public TerrainField Field()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            return _field ??= MapRuntime.BuildField(_doc!, _registry);
        }
    }

    /// <summary>The manifest paths supplied when the document was opened or created (empty when none).</summary>
    public IReadOnlyList<string> ManifestPaths { get { lock (_lock) return _manifests; } }

    /// <summary>The path of the open document, or null when none is open.</summary>
    public string? DocumentPath { get { lock (_lock) return _path; } }

    /// <summary>Whether the open document has unsaved changes.</summary>
    public bool IsDirty { get { lock (_lock) return _dirty; } }

    /// <summary>Whether a document is currently open.</summary>
    public bool HasDocument { get { lock (_lock) return _doc is not null; } }

    MapSummary BuildSummaryLocked()
    {
        MapDocument d = _doc!;
        return new MapSummary(
            d.Id, d.DisplayName, d.FormatVersion,
            d.Bounds.MinX, d.Bounds.MinZ, d.Bounds.MaxX, d.Bounds.MaxZ,
            d.Terrain.Seed, d.Terrain.WaterLevel,
            d.Terrain.Features.Select(f => f.Type).ToArray(),
            d.ScatterLayers.Select(l => l.Name).ToArray(),
            d.CompanionLayers.Select(l => l.Name).ToArray(),
            d.Exclusions.Count, d.ScatterOverrides.Count,
            d.Placements.Count, d.Spawns.Count,
            d.PlayerSpawns.Count, d.PlayerSpawns.Select(s => s.Id).ToArray(),
            d.Regions.Select(r => r.Name).ToArray(),
            _dirty);
    }

    // Tiled false for a monolithic or in-memory document (Tiles null). Windowed reads the index's IsPartial
    // directly rather than "_window is not null", so this stays correct even when a caller set no window but
    // the document happens to be a partial one built some other way. The tile-rect fields are the loaded window
    // when one is set, otherwise null (means "the whole world"), never a scanned whole-document extent: that
    // would need a pass over every entry for a case ("no window") that by definition needs no rect at all.
    WindowStatusResult BuildWindowStatusLocked()
    {
        MapTileIndex? tiles = _doc!.Tiles;
        if (tiles is null)
            return new WindowStatusResult(Tiled: false, Windowed: false,
                null, null, null, null, null, null, null, null, OccupiedCount: 0, LoadedCount: 0);

        if (_window is not { } w)
            return new WindowStatusResult(Tiled: true, Windowed: tiles.IsPartial,
                null, null, null, null, null, null, null, null, tiles.Entries.Count, tiles.LoadedCount);

        RectArea worldMin = MapTileGrid.AreaOf(w.Min, tiles.TileSize);
        RectArea worldMax = MapTileGrid.AreaOf(w.Max, tiles.TileSize);
        return new WindowStatusResult(Tiled: true, Windowed: tiles.IsPartial,
            w.Min.X, w.Min.Z, w.Max.X, w.Max.Z,
            worldMin.MinX, worldMin.MinZ, worldMax.MaxX, worldMax.MaxZ,
            tiles.Entries.Count, tiles.LoadedCount);
    }

    void RequireDocumentLocked()
    {
        if (_doc is null)
            throw new InvalidOperationException("No map document is open. Call map_open or map_create first.");
    }

    static IReadOnlyList<string> CopyManifests(IReadOnlyList<string>? manifestPaths)
        => manifestPaths is null ? Array.Empty<string>() : manifestPaths.ToArray();
}
