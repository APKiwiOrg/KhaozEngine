// TEMP - drop at merge. Throwaway synchronous IPersistenceQueue so Batch 2 item 10 (settings)
// builds and tests before item 8's real coalescing PersistenceQueue lands. At merge the coordinator
// deletes this file (and TempDirectPersistenceQueueTests) and wires item 8's PersistenceQueue in.
using System.IO;

namespace KhaozEngine.Persistence;

/// <summary>
/// TEMP - drop at merge. Writes synchronously (temp file then atomic move); no coalescing.
/// </summary>
public sealed class TempDirectPersistenceQueue : IPersistenceQueue
{
    /// <inheritdoc />
    public void Enqueue(string path, string json)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            // Preserve legacy behavior: swallow persistence errors.
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
        // No-op: Enqueue already writes synchronously.
    }
}
