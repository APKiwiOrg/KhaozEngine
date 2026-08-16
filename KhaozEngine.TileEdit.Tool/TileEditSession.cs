using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Editing;

namespace KhaozEngine.TileEdit;

/// <summary>Holds the one open world. All members lock internally.
///
/// <para>The single stateful object behind the ke-tileedit MCP server: the <see cref="TileEditingDocument"/>
/// being edited, the directory it came from, and the catalog paths it resolved. Every mutation goes through
/// <see cref="Execute"/>, which builds an <see cref="ITileCommand"/> and hands it to the editing document, so an
/// MCP edit carries the same undo, the same collision upkeep and the same dirty rects a GUI edit would.</para>
///
/// <para>Catalog paths in the manifest resolve RELATIVE TO THE WORLD DIRECTORY, never to the process working
/// directory: an MCP server is started by a client whose working directory is its own business, so a world that
/// only loads from one directory would be a world that only loads for one client. Every other path a verb takes
/// follows the same rule through <see cref="ResolvePath"/>, which needs an open world for it.</para>
///
/// <para>ONE VERB IS ONE UNDO STEP. <see cref="Execute"/> seals the gesture after each command, so the drag
/// coalescing the command layer offers never fires over MCP, where every call is a discrete instruction rather
/// than one sample of a held mouse button. The GUI editor of a later round drives
/// <see cref="TileEditingDocument"/> directly and keeps that coalescing for its drag tools.</para></summary>
public sealed class TileEditSession
{
    readonly object _lock = new();

    TileEditingDocument? _editing;
    string? _path;
    IReadOnlyList<string> _catalogPaths = Array.Empty<string>();

    /// <summary>Loads the world directory at <paramref name="path"/>, replacing whatever was open. Every entry
    /// of the manifest's catalog list is resolved against that directory (an absolute entry is taken as it
    /// stands) and loaded before the editing document is built, so a world whose catalogs are missing fails here
    /// rather than half open. There is no dirty guard: the client's git diff is the safety net, and
    /// <see cref="WorldSummary.Dirty"/> reports unsaved state.</summary>
    /// <exception cref="TileWorldException">The world, or one of its catalogs, could not be loaded.</exception>
    public OpenResult Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            TileWorldDocument doc;
            try
            {
                doc = TileWorldFile.Load(path);
            }
            catch (Exception ex) when (ex is not TileWorldException)
            {
                throw new TileWorldException($"{path}: cannot open world. {ex.Message}", ex);
            }
            IReadOnlyList<string> resolved = ResolveCatalogPaths(path, doc.CatalogPaths);
            OpenLocked(doc, TileWorldCatalogs.Load(resolved), path, resolved);
            return new OpenResult(path, doc.Id, doc.DisplayName, BuildSummaryLocked());
        }
    }

    /// <summary>Creates an empty world at <paramref name="path"/> with one region at (0, 0), saves it, and keeps
    /// it open. The catalog paths are stored in the manifest EXACTLY as given, so a relative entry stays
    /// relative and the world stays portable, and they are resolved the same way <see cref="Open"/> resolves
    /// them to load the catalogs now. Refuses a directory that already holds a world: this writes a manifest,
    /// and overwriting one would discard whatever regions it named.</summary>
    /// <exception cref="TileWorldException">A world already exists at <paramref name="path"/>, or one of the
    /// catalogs could not be loaded.</exception>
    public OpenResult Create(string path, string id, string displayName, int planeCount, float tileSize,
        IReadOnlyList<string> catalogPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(catalogPaths);
        ArgumentOutOfRangeException.ThrowIfLessThan(planeCount, 1);
        if (!(tileSize > 0f))
            throw new ArgumentOutOfRangeException(nameof(tileSize), tileSize, "tileSize must be positive.");
        lock (_lock)
        {
            if (TileWorldFile.Exists(path))
                throw new TileWorldException(
                    $"{path}: a world already exists there ({TileWorldFile.ManifestPath(path)}). Open it, or " +
                    "choose a different directory.");

            var doc = new TileWorldDocument
            {
                Id = id,
                DisplayName = displayName ?? "",
                PlaneCount = planeCount,
                TileSize = tileSize,
            };
            foreach (string entry in catalogPaths) doc.CatalogPaths.Add(entry);
            doc.GetOrCreateRegion(new RegionCoord(0, 0));

            IReadOnlyList<string> resolved = ResolveCatalogPaths(path, catalogPaths);
            TileWorldCatalogs catalogs = TileWorldCatalogs.Load(resolved);
            // Validated before the first byte lands, so a bad header (a plane count a region cannot carry, a
            // tile size no load would accept) fails with the validator's message rather than half a world.
            TileWorldValidator.ValidateOrThrow(doc, catalogs);
            TileWorldFile.Save(doc, path, force: true);

            OpenLocked(doc, catalogs, path, resolved);
            return new OpenResult(path, doc.Id, doc.DisplayName, BuildSummaryLocked());
        }
    }

    /// <summary>Validates the open world and, when it passes, writes it back to its own directory and marks the
    /// current history position as saved. An invalid world throws and nothing is written, so the directory never
    /// holds a world this engine would refuse to load.</summary>
    public SaveResult Save()
    {
        lock (_lock)
        {
            TileEditingDocument e = RequireOpenLocked();
            TileWorldValidator.ValidateOrThrow(e.Document, e.Catalogs);
            TileWorldFile.Save(e.Document, _path!);
            e.MarkSaved();
            return new SaveResult(_path!, TileWorldHash.OfWorld(e.Document));
        }
    }

    /// <summary>A flat summary of the open world: identity, geometry, counts, hash and editing state.</summary>
    public WorldSummary Summary()
    {
        lock (_lock)
        {
            RequireOpenLocked();
            return BuildSummaryLocked();
        }
    }

    /// <summary>Validates the open world against its catalogs, reporting every issue as <c>[code] message</c>
    /// rather than throwing, so a client can ask what is wrong without a failed call.</summary>
    public ValidateResult Validate()
    {
        lock (_lock)
        {
            TileEditingDocument e = RequireOpenLocked();
            IReadOnlyList<TileWorldIssue> issues = TileWorldValidator.Validate(e.Document, e.Catalogs);
            return new ValidateResult(issues.Count == 0,
                issues.Select(i => $"[{i.Code}] {i.Message}").ToArray());
        }
    }

    /// <summary>Runs <paramref name="fn"/> against the open editing document under the session lock, which is
    /// how every query and every render reads the world.</summary>
    public T Read<T>(Func<TileEditingDocument, T> fn)
    {
        ArgumentNullException.ThrowIfNull(fn);
        lock (_lock)
        {
            return fn(RequireOpenLocked());
        }
    }

    /// <summary>The one mutation entry point: <paramref name="build"/> reads the open document and returns the
    /// command that expresses the edit, and that command is executed under the same lock acquisition, so nothing
    /// can move between the read and the apply. A builder or an apply that throws propagates untouched, leaving
    /// the document exactly as it was (the command layer's own guarantee), and the returned result carries the
    /// rects the edit touched before they are acknowledged.
    ///
    /// <para>The gesture is SEALED after every command, so each call lands as its own undo step. This is the
    /// one place the tool deliberately parts company with the GUI: coalescing exists so a drag of a hundred
    /// mouse-move events undoes in one go, but over MCP each call is a discrete instruction a client issued on
    /// purpose, and two <c>object_move</c> calls that quietly became one undo step would leave a client unable
    /// to step back through its own edits. A GUI drag tool (R5) drives <see cref="TileEditingDocument"/>
    /// directly and keeps the coalescing.</para></summary>
    /// <exception cref="ArgumentOutOfRangeException">The command reports a dirty rect on a plane the world does
    /// not have.</exception>
    public MutationResult Execute(Func<TileEditingDocument, ITileCommand> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (_lock)
        {
            TileEditingDocument e = RequireOpenLocked();
            ITileCommand command = build(e);
            e.Execute(command);
            // After the execute, not before: the barrier stops the NEXT command merging into this one, and
            // raising it first would only stop this one merging into whatever came before.
            e.SealGesture();
            DirtyRectInfo[] rects = TakeRebuildsLocked(e);
            return new MutationResult(command.Label, e.IsDirty, e.History.UndoDepth,
                TileWorldHash.OfWorld(e.Document), rects);
        }
    }

    /// <summary>Undoes up to <paramref name="steps"/> commands, stopping early when the stack runs out, and
    /// reports how many actually moved.</summary>
    public UndoResult Undo(int steps = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        lock (_lock)
        {
            TileEditingDocument e = RequireOpenLocked();
            int moved = 0;
            while (moved < steps && e.Undo()) moved++;
            return BuildUndoResultLocked(e, moved);
        }
    }

    /// <summary>Redoes up to <paramref name="steps"/> commands, stopping early when the stack runs out, and
    /// reports how many actually moved.</summary>
    public UndoResult Redo(int steps = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        lock (_lock)
        {
            TileEditingDocument e = RequireOpenLocked();
            int moved = 0;
            while (moved < steps && e.Redo()) moved++;
            return BuildUndoResultLocked(e, moved);
        }
    }

    /// <summary>Ends the current gesture. <see cref="Execute"/> already seals after every command, so over MCP
    /// this is a no-op kept for the callers that want to say so explicitly.</summary>
    public void SealGesture()
    {
        lock (_lock) RequireOpenLocked().SealGesture();
    }

    /// <summary>Throws when no world is open, naming the two verbs that open one. Every other member calls this
    /// for itself, so a client never has to.</summary>
    /// <exception cref="TileWorldException">No world is open.</exception>
    public void RequireOpen()
    {
        lock (_lock) RequireOpenLocked();
    }

    /// <summary>Turns a caller-supplied path into a normalised absolute one: a relative path resolves against
    /// the open world's directory, the same rule the manifest's catalog entries follow, and a rooted one is
    /// normalised as it stands so an echoed path never carries a <c>..</c> segment back to the client.
    ///
    /// <para>Requires an open world, which is the point rather than a side effect: every verb that touches the
    /// filesystem comes through here, and without a world there is no directory to be relative TO. Falling back
    /// to the process working directory would let a closed session read and enumerate whatever the MCP client
    /// happened to launch the server in.</para></summary>
    /// <exception cref="TileWorldException">No world is open.</exception>
    public string ResolvePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        lock (_lock)
        {
            RequireOpenLocked();
            return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(_path!, path));
        }
    }

    /// <summary>Whether a world is currently open.</summary>
    public bool HasDocument { get { lock (_lock) return _editing is not null; } }

    /// <summary>The directory of the open world, or null when none is open.</summary>
    public string? DocumentPath { get { lock (_lock) return _path; } }

    /// <summary>The catalog paths the open world resolved to, absolute, in manifest order.</summary>
    public IReadOnlyList<string> CatalogPaths { get { lock (_lock) return _catalogPaths; } }

    /// <summary>The open editing document, or null when none is open. Exposed for the tests and for the verbs
    /// that need the document's own types: everything that MUTATES still goes through <see cref="Execute"/>.</summary>
    public TileEditingDocument? Editing { get { lock (_lock) return _editing; } }

    // Every catalog entry as an absolute path. A relative one is taken against the WORLD DIRECTORY, so the same
    // manifest loads from any working directory, and Path.GetFullPath then normalises the "../shared/x.json"
    // form a world beside its catalogs naturally uses.
    static IReadOnlyList<string> ResolveCatalogPaths(string worldDirectory, IEnumerable<string> entries) =>
        entries.Select(e => Path.IsPathRooted(e) ? e : Path.GetFullPath(Path.Combine(worldDirectory, e))).ToArray();

    void OpenLocked(TileWorldDocument doc, TileWorldCatalogs catalogs, string path, IReadOnlyList<string> resolved)
    {
        _editing = new TileEditingDocument(doc, catalogs);
        _path = path;
        _catalogPaths = resolved;
    }

    // The rects accumulated since the last acknowledgement, handed to the caller and cleared. Every mutation
    // path takes them, so one verb's report can never carry the rects of the verb before it.
    static DirtyRectInfo[] TakeRebuildsLocked(TileEditingDocument e)
    {
        DirtyRectInfo[] rects = e.PendingRebuilds
            .Select(d => new DirtyRectInfo(RectInfo.Of(d.Rect), d.Plane)).ToArray();
        e.AcknowledgeRebuilds();
        return rects;
    }

    UndoResult BuildUndoResultLocked(TileEditingDocument e, int moved)
    {
        TakeRebuildsLocked(e);
        return new UndoResult(moved, e.IsDirty, e.History.UndoDepth, e.History.RedoDepth,
            e.History.UndoLabel, e.History.RedoLabel, TileWorldHash.OfWorld(e.Document));
    }

    WorldSummary BuildSummaryLocked()
    {
        TileEditingDocument e = _editing!;
        TileWorldDocument d = e.Document;
        return new WorldSummary(d.Id, d.DisplayName, _path!,
            d.PlaneCount, d.TileSize, d.Regions.Count, d.AllObjects().Count(), d.AllMarkers().Count(),
            TileWorldHash.OfWorld(d), e.IsDirty, e.History.UndoDepth, e.History.RedoDepth,
            e.History.UndoLabel, e.History.RedoLabel, d.CatalogPaths.ToArray());
    }

    TileEditingDocument RequireOpenLocked() =>
        _editing ?? throw new TileWorldException("no world is open, call world_open or world_create first");
}
