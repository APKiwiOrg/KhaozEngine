using System.IO;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

// TEMP - drop at merge alongside TempDirectPersistenceQueue.
public class TempDirectPersistenceQueueTests
{
    [Fact]
    public void Enqueue_WritesJsonAtomically_AndCleansTempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-item10-" + Path.GetRandomFileName());
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var queue = new TempDirectPersistenceQueue();

            queue.Enqueue(path, "{\"a\":1}");

            Assert.True(File.Exists(path));
            Assert.Equal("{\"a\":1}", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Enqueue_OverwritesExistingFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-item10-" + Path.GetRandomFileName());
        try
        {
            string path = Path.Combine(dir, "settings.json");
            var queue = new TempDirectPersistenceQueue();

            queue.Enqueue(path, "{\"v\":1}");
            queue.Enqueue(path, "{\"v\":2}");

            Assert.Equal("{\"v\":2}", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
