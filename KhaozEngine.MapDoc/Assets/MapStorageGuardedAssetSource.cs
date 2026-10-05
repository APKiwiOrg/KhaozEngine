using System;
using System.IO;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>Reads resources under a stored document's resource root, refusing any reference inside the namespace
/// its storage writer owns before reading it. Every closure reference goes through <see cref="Read"/>, including
/// roots, unused declarations and transitive dependencies, so a verified closure can never contain a file the next
/// save would sweep or overwrite.</summary>
public sealed class MapStorageGuardedAssetSource : IMapAssetSource
{
    readonly string _storagePath;
    readonly MapDocumentForm _form;
    readonly string _root;
    readonly MapDirectoryAssetSource _inner;

    /// <param name="storagePath">The absolute monolithic file or tiled directory the document is, or will be, written to.</param>
    /// <param name="form">The storage form at <paramref name="storagePath"/>, stated explicitly by the caller.</param>
    public MapStorageGuardedAssetSource(string storagePath, MapDocumentForm form)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (!Path.IsPathFullyQualified(storagePath))
            throw new ArgumentException("An absolute storage path is required.", nameof(storagePath));
        _storagePath = storagePath;
        _form = form;
        _root = MapDocumentStorage.ResourceRoot(storagePath, form);
        _inner = new MapDirectoryAssetSource(_root);
    }

    public ReadOnlyMemory<byte> Read(MapAssetRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string path;
        try
        {
            // The same resolution the directory source uses.
            path = Path.GetFullPath(reference.Path, _root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return _inner.Read(reference);
        }
        if (MapDocumentStorage.IsReserved(_storagePath, _form, path))
            throw new MapDocumentException(
                $"Native resource '{reference.Id}' at '{path}' is inside storage reserved for the {_form} map writer " +
                $"at '{_storagePath}'. Move it outside the map's manifest, lock, temp and tiles paths.");
        return _inner.Read(reference);
    }
}
