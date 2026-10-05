using System;
using System.IO;

namespace KhaozEngine.MapDoc;

/// <summary>Where a stored map document keeps its native resources, and which paths its storage writer owns.
/// A tiled writer owns its manifest, manifest temp, save lock and the whole <c>tiles</c> subtree, and sweeps
/// unindexed files there on save. A monolithic writer owns only its document file. Paths are normalized with
/// <see cref="Path.GetFullPath(string)"/>, so dot segments resolve before comparison, and compared with the same
/// platform case policy as the tiled writer. Symbolic links and other filesystem aliases are not resolved.</summary>
public static class MapDocumentStorage
{
    /// <summary>The resource root for a document stored at <paramref name="storagePath"/>: a monolithic file's
    /// directory, or the tiled document's own directory. Returned fully qualified.</summary>
    public static string ResourceRoot(string storagePath, MapDocumentForm form)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        string full = MapTiledFile.Normalize(storagePath);
        return form switch
        {
            MapDocumentForm.Tiled => full,
            MapDocumentForm.Monolithic => Path.GetDirectoryName(full)
                ?? throw new ArgumentException($"{storagePath}: a monolithic document needs a parent directory.", nameof(storagePath)),
            _ => throw new ArgumentException("A stored document has a tiled or monolithic form.", nameof(form)),
        };
    }

    /// <summary>Whether <paramref name="directory"/> already holds a tiled map, meaning its manifest exists. Unlike
    /// <see cref="MapDocumentFile.DetectForm"/>, a directory holding only other files, such as prepared native
    /// resources, does not count.</summary>
    public static bool HoldsTiledDocument(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return File.Exists(Path.Combine(MapTiledFile.Normalize(directory), MapTiledFile.ManifestName));
    }

    /// <summary>Whether <paramref name="path"/> lies in the namespace the <paramref name="form"/> writer owns for a
    /// document stored at <paramref name="storagePath"/>. Such a path must never hold an authored resource.</summary>
    public static bool IsReserved(string storagePath, MapDocumentForm form, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = MapTiledFile.Normalize(storagePath);
        string candidate = MapTiledFile.Normalize(path);
        StringComparison comparison = MapTiledFile.PathComparison;
        if (form == MapDocumentForm.Monolithic) return string.Equals(candidate, root, comparison);
        if (form != MapDocumentForm.Tiled)
            throw new ArgumentException("A stored document has a tiled or monolithic form.", nameof(form));

        foreach (string owned in new[] { MapTiledFile.ManifestName, MapTiledFile.ManifestTempName, MapTiledSaveLock.FileName })
            if (string.Equals(candidate, Path.Combine(root, owned), comparison)) return true;
        string tiles = Path.Combine(root, MapTileFile.TilesDirectory);
        return string.Equals(candidate, tiles, comparison)
            || candidate.StartsWith(tiles + Path.DirectorySeparatorChar, comparison);
    }
}
