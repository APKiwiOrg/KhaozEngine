using System;
using System.IO;

namespace KhaozEngine.MapDoc.Assets;

/// <summary>Supplies exact resource bytes. The closure snapshots and verifies each returned buffer.</summary>
public interface IMapAssetSource
{
    ReadOnlyMemory<byte> Read(MapAssetRef reference);
}

/// <summary>Reads resource paths against an explicit absolute root. Absolute references remain absolute.</summary>
public sealed class MapDirectoryAssetSource : IMapAssetSource
{
    readonly string _root;

    public MapDirectoryAssetSource(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("An absolute asset root is required.", nameof(root));
        _root = Path.GetFullPath(root);
    }

    public ReadOnlyMemory<byte> Read(MapAssetRef reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        try
        {
            return File.ReadAllBytes(Path.GetFullPath(reference.Path, _root));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new MapDocumentException($"Cannot read native resource '{reference.Id}' at '{reference.Path}'.", ex);
        }
    }
}
