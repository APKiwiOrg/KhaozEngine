using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.Content;
using KhaozEngine.MapDoc;
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

    /// <summary>Loads the document at <paramref name="path"/> (default registry), replacing any open document.
    /// There is no dirty guard: the client's git diff is the safety net, but <see cref="MapSummary.Dirty"/>
    /// reports unsaved state. Throws <see cref="MapDocumentException"/> (naming the path) on any load failure.</summary>
    public OpenResult Open(string path, IReadOnlyList<string>? manifestPaths = null)
    {
        lock (_lock)
        {
            MapDocument doc = MapDocumentFile.Load(path);
            _doc = doc;
            _path = path;
            _manifests = CopyManifests(manifestPaths);
            _dirty = false;
            _field = null;
            return new OpenResult(path, doc.Id, doc.DisplayName, BuildSummaryLocked());
        }
    }

    /// <summary>Creates a fresh document with one default all-open Meadow biome band (so scatter rules have a
    /// biome to bind to), validates and saves it, and keeps it open. Creates parent directories. Throws
    /// <see cref="IOException"/> when the file already exists and <paramref name="overwrite"/> is false.</summary>
    public OpenResult Create(string path, string id, string displayName,
        float minX, float minZ, float maxX, float maxZ,
        int seed = 1, float waterLevel = 0f, bool overwrite = false,
        IReadOnlyList<string>? manifestPaths = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            if (File.Exists(path) && !overwrite)
                throw new IOException($"{path}: file already exists. Pass overwrite to replace it.");

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
            return new OpenResult(path, doc.Id, doc.DisplayName, BuildSummaryLocked());
        }
    }

    /// <summary>Saves the open document to its path (validates first, throwing on invalid) and clears dirty.</summary>
    public SaveResult Save()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            MapDocumentFile.Save(_doc!, _path!, _registry);
            _dirty = false;
            return new SaveResult(_path!, true);
        }
    }

    /// <summary>Validates the open document. Structural = <see cref="MapDocumentValidator"/>. When structural
    /// passes, schema = <see cref="JsonSchemaValidator"/> over the serialized document against the packaged
    /// schema. When structural fails the schema check is skipped (serialization would throw) and its errors
    /// carry a note.</summary>
    public ValidateResult Validate()
    {
        lock (_lock)
        {
            RequireDocumentLocked();
            IReadOnlyList<string> structuralErrors = MapDocumentValidator.Validate(_doc!, _registry);
            bool structuralValid = structuralErrors.Count == 0;
            if (!structuralValid)
            {
                return new ValidateResult(false, structuralErrors, false,
                    new[] { "schema check skipped because the document is structurally invalid." });
            }

            ValidationReport report = JsonSchemaValidator.Validate(
                MapDocumentFile.SaveText(_doc!, _registry), MapDocumentSchema.GetJson());
            return new ValidateResult(true, Array.Empty<string>(), report.IsValid, report.Errors);
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
            d.Regions.Select(r => r.Name).ToArray(),
            _dirty);
    }

    void RequireDocumentLocked()
    {
        if (_doc is null)
            throw new InvalidOperationException("No map document is open. Call map_open or map_create first.");
    }

    static IReadOnlyList<string> CopyManifests(IReadOnlyList<string>? manifestPaths)
        => manifestPaths is null ? Array.Empty<string>() : manifestPaths.ToArray();
}
