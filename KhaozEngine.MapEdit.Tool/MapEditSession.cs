using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
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
    MapAssetClosure? _nativeAssets;
    string? _path;
    // The absolute storage path and its known form. Native resources resolve under its resource root, and native
    // writes target exactly this form, never one inferred from whatever is at the path later.
    string? _storagePath;
    MapDocumentForm _storageForm;
    IReadOnlyList<string> _manifests = Array.Empty<string>();
    bool _dirty;
    TerrainField? _field;
    MapTileRect? _window;

    /// <summary>Binds a verified closure to the current document. Does not mark dirty. Native open, window
    /// replacement, conversion and retile bind their own freshly verified closure, and replacing the document with
    /// an analytic one clears it.</summary>
    public void BindNativeAssets(MapAssetClosure assets)
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            MapBoundDocumentValidation.Validate(_doc!, assets, _registry);
            _nativeAssets = assets;
        }
    }

    internal void ApplyNative(EditorCommand command, MapDocument document, MapDocRegistry registry) =>
        NativePlacementTransaction.Run(document, command, false, _nativeAssets, registry);

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
    /// reports unsaved state. Throws <see cref="MapDocumentException"/> (naming the path) on any load failure.
    /// A native document must load whole and pass <see cref="NativeDocumentService"/> validation against its
    /// resource root (the file's directory, or the tiled directory) before it replaces anything. Its path and
    /// root are anchored as absolute paths, and its verified closure is bound for editing.</summary>
    public OpenResult Open(string path, IReadOnlyList<string>? manifestPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            string fullPath = Path.GetFullPath(path);
            var options = new MapDocumentLoadOptions { Registry = _registry };
            MapDocument doc = MapDocumentWindowing.Load(fullPath, options, WholeWorldTileLimit, EditorWindowRadius,
                out _, out MapTileRect? window);
            MapDocumentForm form = MapDocumentFile.DetectForm(fullPath);
            MapResolvedDocument? resolved = NativeDocumentService.IsNative(doc)
                ? NativeDocumentService.Verify(doc, fullPath, form, path)
                : null;
            _doc = doc;
            _nativeAssets = resolved?.AssetClosure;
            _path = resolved is null ? path : fullPath;
            _storagePath = fullPath;
            _storageForm = form;
            _manifests = CopyManifests(manifestPaths);
            _dirty = false;
            _field = null;
            _window = window;
            return new OpenResult(_path, doc.Id, doc.DisplayName, BuildSummaryLocked(resolved));
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
            _nativeAssets = null;
            _path = path;
            _storagePath = Path.GetFullPath(path);
            _storageForm = MapDocumentForm.Monolithic;
            _manifests = CopyManifests(manifestPaths);
            _dirty = false;
            _field = null;
            _window = null;
            return new OpenResult(path, doc.Id, doc.DisplayName, BuildSummaryLocked(null));
        }
    }

    /// <summary>Saves the open document back to its path, in the form it was opened or last converted into
    /// (<see cref="MapDocumentFile.SaveAuto"/>): a tiled directory saves tiled, a monolithic file saves
    /// monolithic, never converting implicitly. Validates first, throwing on invalid, and clears dirty on
    /// success. A native document is first verified against its anchored resource root, so a stale or missing
    /// resource or one inside writer-owned storage refuses before any byte is written. A native save keeps the
    /// form the document was opened or converted in: vanished or replaced tiled storage refuses, a missing
    /// monolithic file is recreated, and a monolithic native save is staged and promoted atomically.</summary>
    public SaveResult Save()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            if (NativeDocumentService.IsNative(_doc!))
            {
                NativeDocumentService.RequireStorage(_storagePath!, _storageForm, allowMissingFile: true);
                MapResolvedDocument resolved = NativeDocumentService.Verify(_doc!, _storagePath!, _storageForm, _path!);
                NativeDocumentService.Save(_doc!, _storagePath!, _storageForm, _registry);
                _nativeAssets = resolved.AssetClosure;
            }
            else if (MapDocumentFile.DetectForm(_path!) == MapDocumentForm.None)
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
    /// the dirty flag are what actually need resetting, and this does both. A native candidate must load whole
    /// and verify against the directory's resources before it replaces the session, then binds that closure.</summary>
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
            MapDocument candidate = MapDocumentFile.LoadTiled(directory, rect, options);
            string storage = Path.GetFullPath(directory);
            MapAssetClosure? assets = NativeDocumentService.IsNative(candidate)
                ? NativeDocumentService.Verify(candidate, storage, MapDocumentForm.Tiled, directory).AssetClosure
                : null;
            _doc = candidate;
            _nativeAssets = assets;
            _path = directory;
            _storagePath = storage;
            _storageForm = MapDocumentForm.Tiled;
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
    /// swept the rest away). A native document is verified against the destination directory's resources and its
    /// tiled storage namespace before anything is written, whatever the source form. Missing, stale or
    /// writer-owned resources refuse, they are never copied or rebased.</summary>
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
            bool native = NativeDocumentService.IsNative(_doc!);
            string target = native ? Path.GetFullPath(directory) : directory;
            string storage = Path.GetFullPath(directory);
            // The tiled target's namespace is checked even when the source is monolithic.
            MapAssetClosure? assets = native
                ? NativeDocumentService.Verify(_doc!, storage, MapDocumentForm.Tiled, directory).AssetClosure
                : null;
            MapDocumentFile.SaveAs(_doc!, target, MapDocumentForm.Tiled, _registry);
            if (native) _nativeAssets = assets;
            _path = target;
            _storagePath = storage;
            _storageForm = MapDocumentForm.Tiled;
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
    /// <see cref="MapDocument.TileSize"/> is preserved exactly. A native document is verified against the
    /// destination file's directory first and written through a staged sibling file.</summary>
    public ConvertResult ConvertToSingle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            RequireDocumentLocked();
            if (MapDocumentFile.DetectForm(path) == MapDocumentForm.Tiled)
                throw new MapDocumentException(
                    $"{path}: a tiled document (a directory) already exists there. Convert or delete it first.");
            bool native = NativeDocumentService.IsNative(_doc!);
            string target = native ? Path.GetFullPath(path) : path;
            string storage = Path.GetFullPath(path);
            MapAssetClosure? assets = native
                ? NativeDocumentService.Verify(_doc!, storage, MapDocumentForm.Monolithic, path).AssetClosure
                : null;
            if (native) NativeDocumentService.SaveMonolithicStaged(_doc!, target, _registry);
            else MapDocumentFile.SaveAs(_doc!, path, MapDocumentForm.Monolithic, _registry);
            _doc!.Tiles = null;   // now genuinely monolithic, matching what a fresh MapDocumentFile.Load gives.
            if (native) _nativeAssets = assets;
            _path = target;
            _storagePath = storage;
            _storageForm = MapDocumentForm.Monolithic;
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
            MapAssetClosure? assets = null;
            try
            {
                if (NativeDocumentService.IsNative(_doc))
                {
                    // Retile keeps refusing absent storage, as the analytic SaveAuto path does.
                    NativeDocumentService.RequireStorage(_storagePath!, _storageForm, allowMissingFile: false);
                    assets = NativeDocumentService.Verify(_doc, _storagePath!, _storageForm, _path!).AssetClosure;
                    NativeDocumentService.Save(_doc, _storagePath!, _storageForm, _registry);
                }
                else MapDocumentFile.SaveAuto(_doc, _path!, _registry);
            }
            catch
            {
                // A rejected save must not leave the session holding an in-memory tileSize that was never
                // written: restore it, so the document (and IsDirty) still describe what is actually on disk.
                _doc.TileSize = oldTileSize;
                throw;
            }
            if (assets is not null) _nativeAssets = assets;
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

    /// <summary>Validates the open document structurally, then schema-checks either the whole document or each
    /// loaded tile in a partial document. When <paramref name="verifyWholeWorld"/> is true, a tiled source also
    /// runs <see cref="MapDocumentFile.VerifyTiled"/> against every tile on disk without widening or mutating
    /// the loaded window. A native document also runs fresh complete closure validation, reported as closure
    /// findings and a false <see cref="ValidateResult.Valid"/> instead of an exception.</summary>
    public ValidateResult Validate(bool verifyWholeWorld = false)
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            IReadOnlyList<string> structuralErrors = MapDocumentValidator.Validate(_doc!, _registry);
            bool structuralValid = structuralErrors.Count == 0;
            ValidateResult result;
            if (!structuralValid)
            {
                result = new ValidateResult(false, structuralErrors, SchemaChecked: false, SchemaValid: false,
                    new[] { "schema check skipped because the document is structurally invalid." })
                {
                    Valid = false,
                    SchemaScope = "none",
                };
            }
            else if (_doc!.Tiles is { IsPartial: true })
            {
                ValidationReport report = MapEditSchemaValidation.ValidateLoadedTiles(_doc, _registry);
                result = new ValidateResult(true, Array.Empty<string>(), SchemaChecked: true,
                    report.IsValid, report.Errors)
                {
                    Valid = report.IsValid,
                    SchemaScope = "loadedTiles",
                };
            }
            else
            {
                ValidationReport report = JsonSchemaValidator.Validate(
                    MapDocumentFile.SaveText(_doc!, _registry), MapDocumentSchema.GetJson());
                result = new ValidateResult(true, Array.Empty<string>(), SchemaChecked: true,
                    report.IsValid, report.Errors)
                {
                    Valid = report.IsValid,
                    SchemaScope = "document",
                };
            }

            if (NativeDocumentService.IsNative(_doc!)) result = WithClosureLocked(result);
            if (!verifyWholeWorld) return result;

            if (_doc!.Tiles?.SourceDirectory is not { } directory)
            {
                return result with
                {
                    Valid = false,
                    WholeWorldChecked = false,
                    WholeWorldValid = false,
                    WholeWorldErrors = new[]
                    {
                        "whole-world verification requires an open tiled document backed by a directory.",
                    },
                };
            }

            IReadOnlyList<string> wholeWorldErrors = MapDocumentFile.VerifyTiled(directory, _registry);
            bool wholeWorldValid = wholeWorldErrors.Count == 0;
            return result with
            {
                Valid = result.Valid && wholeWorldValid,
                WholeWorldChecked = true,
                WholeWorldValid = wholeWorldValid,
                WholeWorldErrors = wholeWorldErrors,
            };
        }
    }

    /// <summary>A flat summary of the open document (counts, names in fold order, dirty flag). A native document
    /// is verified fresh first, so a stale or incomplete closure throws <see cref="MapDocumentException"/> rather
    /// than reporting a native identity.</summary>
    public MapSummary Summary()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            MapResolvedDocument? resolved = NativeDocumentService.IsNative(_doc!)
                ? NativeDocumentService.Verify(_doc!, _storagePath!, _storageForm, _path!)
                : null;
            return BuildSummaryLocked(resolved);
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
            T result;
            if (_doc!.ResolverIdentity is not null)
            {
                if (_nativeAssets is null) throw new MapDocumentException("Native editing requires an explicit asset closure binding.");
                MapDocument candidate = NativeDocumentSnapshot.Clone(_doc, _registry);
                result = fn(candidate, _registry);
                MapBoundDocumentValidation.Validate(candidate, _nativeAssets, _registry);
                NativeDocumentSnapshot.Publish(_doc, candidate);
            }
            else result = fn(_doc, _registry);
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

    ValidateResult WithClosureLocked(ValidateResult result)
    {
        try
        {
            NativeDocumentService.Verify(_doc!, _storagePath!, _storageForm, _path!);
            return result with { ClosureChecked = true, ClosureValid = true };
        }
        catch (MapDocumentException ex)
        {
            return result with { Valid = false, ClosureChecked = true, ClosureValid = false, ClosureErrors = new[] { ex.Message } };
        }
    }

    MapSummary BuildSummaryLocked(MapResolvedDocument? resolved)
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
            _dirty)
        {
            Native = resolved is null ? null : NativeDocumentService.Summarize(d, resolved),
        };
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
