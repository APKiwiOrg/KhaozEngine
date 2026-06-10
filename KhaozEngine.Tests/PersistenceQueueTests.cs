using System;
using System.IO;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public class PersistenceQueueTests
{
    [Fact]
    public void EnqueueThenFlush_WritesFileToDisk()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(path, "payload");
            queue.Flush();

            Assert.True(File.Exists(path));
            Assert.Equal("payload", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Enqueue_SamePathRepeatedly_LastWriteWins()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(path, "a");
            queue.Enqueue(path, "b");
            queue.Enqueue(path, "c");
            queue.Flush();

            Assert.Equal("c", File.ReadAllText(path));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Enqueue_DifferentPaths_BothWritten()
    {
        string root = NewTempRoot();
        try
        {
            string p1 = Path.Combine(root, "save.json");
            string p2 = Path.Combine(root, "settings.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(p1, "one");
            queue.Enqueue(p2, "two");
            queue.Flush();

            Assert.Equal("one", File.ReadAllText(p1));
            Assert.Equal("two", File.ReadAllText(p2));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Dispose_FlushesPendingWrite_AndBlocksFurtherEnqueue()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            var queue = new PersistenceQueue();

            queue.Enqueue(path, "data");
            queue.Dispose();

            Assert.Equal("data", File.ReadAllText(path));
            Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(path, "more"));
        }
        finally { Cleanup(root); }
    }

    internal static string NewTempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ke-persistqueue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void Cleanup(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch { /* best effort */ }
    }
}
