using System;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.Terrain;

namespace KhaozEngine.MapEdit;

/// <summary>Identity and counts of a freshly verified native document. The int64 high-water mark travels as an
/// exact decimal string so JSON clients that parse numbers as doubles cannot round it. Int32 counts stay JSON
/// numbers.</summary>
public sealed record NativeDocumentSummary(string AuthoredHash, int PlacementCount, int NumericIdCount,
    [property: JsonConverter(typeof(MapNumericIdJsonConverter))] long NumericIdHighWaterMark, string ClosureHash);

/// <summary>Complete native validation for every lifecycle boundary: open, window replacement, save, validate,
/// summary, conversion and retile. Each call reloads and digest-verifies the whole closure from the resource
/// root, so a stale or missing resource refuses before any session state is replaced or any byte is written.</summary>
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

    /// <summary>The resource root for a document at an absolute path: a monolithic file's parent directory, or
    /// the tiled document's own directory.</summary>
    internal static string ResourceRootFor(string absolutePath, MapDocumentForm form) =>
        form == MapDocumentForm.Tiled
            ? absolutePath
            : Path.GetDirectoryName(absolutePath) ?? throw new MapDocumentException($"{absolutePath}: no parent directory to hold native resources.");

    /// <summary>Validates against the session's anchored resource root, naming the document and root on failure.</summary>
    internal static MapResolvedDocument Verify(MapDocument document, string resourceRoot, string context)
    {
        if (document.Tiles is { IsPartial: true })
            throw new MapDocumentException(
                $"{context}: a native document must load every tile. Windowed native editing is not supported " +
                "yet, so open it whole (raise WholeWorldTileLimit) or move the window over the whole world.");
        try
        {
            return ValidateComplete(document, new MapDirectoryAssetSource(resourceRoot), SessionOptions);
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

    /// <summary>Writes a verified native document back in its current form. A tiled directory keeps the tiled
    /// writer and its guards. Anything else is written monolithic through a staged sibling file.</summary>
    internal static void Save(MapDocument document, string path, MapDocRegistry registry)
    {
        if (MapDocumentFile.DetectForm(path) == MapDocumentForm.Tiled) MapDocumentFile.SaveAuto(document, path, registry);
        else SaveMonolithicStaged(document, path, registry);
    }

    /// <summary>Serializes into a sibling staging file and promotes it with one rename, so a failed write never
    /// truncates or half-writes the existing document.</summary>
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
