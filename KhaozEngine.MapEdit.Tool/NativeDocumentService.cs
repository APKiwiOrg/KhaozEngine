using System;
using System.IO;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Complete native validation for every lifecycle boundary: open, window replacement, save, validate,
/// summary, conversion and retile. Each call reloads and digest-verifies the whole closure from the resource
/// root, so a stale or missing resource refuses before any session state is replaced or any byte is written.
/// Session checks name the storage form explicitly and refuse any closure reference inside the namespace that
/// form's writer owns (<see cref="MapDocumentStorage"/>).</summary>
public static class NativeDocumentService
{
    /// <summary>The session's single build identity. R1 native documents take placement support heights from the
    /// analytic <see cref="MapRuntime.BuildField"/> field over the default registry. A later round that supplies
    /// a canonical authored sampler changes this identity rather than falling back silently.</summary>
    public static MapResolveOptions SessionOptions { get; } =
        new("khaozengine.mapedit.analytic-support", 1, "mapruntime-buildfield-default-registry-v1");

    /// <summary>Checks the complete document locally, loads and verifies its whole asset closure from
    /// <paramref name="source"/>, then resolves it with analytic support heights. Throws
    /// <see cref="MapDocumentException"/> on a partial, invalid or stale document. Never returns a partial result.</summary>
    public static MapResolvedDocument ValidateComplete(MapDocument document, IMapAssetSource source, MapResolveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        MapDocRegistry registry = MapDocRegistry.CreateDefault();
        // Local checks first, so a partial or malformed document never reads a resource.
        MapBoundDocumentValidation.ValidateLocal(document, registry);
        MapAssetClosure closure = MapAssetClosure.Load(document.NativeAssets, source);
        TerrainField field = MapRuntime.BuildField(document, registry);
        return MapResolver.Resolve(document, closure, field.SampleHeight, options);
    }

    /// <summary>Native opt-in is a non-null resolver identity. Legacy analytic documents keep their old path.</summary>
    internal static bool IsNative(MapDocument document) => document.ResolverIdentity is not null;

    /// <summary>Validates a document for storage at <paramref name="storagePath"/> in the explicit
    /// <paramref name="form"/>, reading resources under that storage's resource root through
    /// <see cref="MapStorageGuardedAssetSource"/>. Names the document and root on failure.</summary>
    internal static MapResolvedDocument Verify(MapDocument document, string storagePath, MapDocumentForm form, string context)
    {
        string resourceRoot = MapDocumentStorage.ResourceRoot(storagePath, form);
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException(
                $"{context}: a native document must load every tile. Windowed native editing is not supported " +
                "yet, so open it whole (raise WholeWorldTileLimit) or move the window over the whole world.");
        try
        {
            return ValidateComplete(document, new MapStorageGuardedAssetSource(Path.GetFullPath(storagePath), form), SessionOptions);
        }
        catch (MapDocumentException ex)
        {
            throw new MapDocumentException(
                $"{context}: native document failed complete validation against resources under '{resourceRoot}'. {ex.Message}", ex);
        }
    }

    internal static NativeDocumentSummary Summarize(MapDocument document, MapResolvedDocument resolved) =>
        new(resolved.AuthoredHash, resolved.Placements.Count, resolved.Placements.Count(p => p.NumericId is not null),
            document.NumericIdHighWaterMark, resolved.AssetClosure.Hash);

    /// <summary>Refuses unless storage at <paramref name="path"/> still has the session's known
    /// <paramref name="form"/>. A missing monolithic file is accepted only when <paramref name="allowMissingFile"/>,
    /// the recovery Save keeps. A form is never inferred from whatever is now at the path.</summary>
    internal static void RequireStorage(string path, MapDocumentForm form, bool allowMissingFile)
    {
        MapDocumentForm actual = MapDocumentFile.DetectForm(path);
        if (actual == form || (allowMissingFile && form == MapDocumentForm.Monolithic && actual == MapDocumentForm.None)) return;
        throw new MapDocumentException(actual == MapDocumentForm.None
            ? $"{path}: the {form} storage this document was opened from no longer exists. Nothing was written."
            : $"{path}: expected {form} storage but found {actual}. Nothing was written.");
    }

    /// <summary>Writes a verified native document in its known <paramref name="form"/>. Tiled storage keeps the
    /// tiled writer and all its guards. Monolithic storage is written through <see cref="SaveMonolithicStaged"/>.</summary>
    internal static void Save(MapDocument document, string path, MapDocumentForm form, MapDocRegistry registry)
    {
        if (form == MapDocumentForm.Tiled) MapDocumentFile.SaveTiled(document, path, registry);
        else SaveMonolithicStaged(document, path, registry);
    }

    /// <summary>Serializes into a sibling staging file and promotes it with one rename, so a failed write never
    /// truncates or half-writes the existing document. The rename replaces the destination directory entry: an
    /// existing symbolic link at the path is replaced rather than written through, and the new file takes default
    /// mode and ACL metadata rather than the previous entry's.</summary>
    internal static void SaveMonolithicStaged(MapDocument document, string path, MapDocRegistry registry)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new MapDocumentException($"{path}: no parent directory.");
        string staging = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                MapDocumentFile.SaveTo(document, stream, registry);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
        }
        catch
        {
            File.Delete(staging);
            throw;
        }
    }
}
