using System.IO;

namespace KhaozEngine.MapDoc;

/// <summary>One writer owns a tiled directory through its final sweep. The empty lock file stays in place:
/// deleting it on release would let an opener lock the old file while a new writer locks its replacement.</summary>
internal static class MapTiledSaveLock
{
    internal const string FileName = ".mapdoc-save.lock";

    internal static FileStream Acquire(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, FileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new MapDocumentException(
                $"{directory}: could not acquire exclusive save access. Another writer may be saving this " +
                "directory. Retry after it finishes.", ex);
        }
    }
}
