using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Diagnostics;
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
    public async Task RapidInterleavedEnqueues_FlushDrainsEverything_NoHang()
    {
        string root = NewTempRoot();
        try
        {
            using var queue = new PersistenceQueue();
            const int n = 2000;
            const int paths = 8;
            for (int i = 0; i < n; i++)
            {
                queue.Enqueue(Path.Combine(root, $"f{i % paths}.json"), $"v{i}");
            }

            // The schedule-on-demand drain has a window where an Enqueue can land just as the worker
            // is exiting; if mishandled it strands that write and Flush() wedges forever. Guard with a
            // timeout so a regression fails loudly instead of hanging the suite.
            var flushTask = Task.Run(() => queue.Flush());
            bool flushed = await Task.WhenAny(flushTask, Task.Delay(TimeSpan.FromSeconds(15))) == flushTask;
            Assert.True(flushed, "Flush() did not complete - the queue wedged");
            await flushTask;   // observe any exception thrown by Flush()

            // Per-path last-writer-wins: the final value for path p is the largest i with i % paths == p.
            for (int p = 0; p < paths; p++)
            {
                int last = n - paths + p;   // n % paths == 0
                Assert.Equal($"v{last}", File.ReadAllText(Path.Combine(root, $"f{p}.json")));
            }
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

    [Fact]
    public void Enqueue_PermanentFailure_RaisesWriteFailedAndLogsAndDoesNotThrow()
    {
        string root = NewTempRoot();
        try
        {
            // Make the parent path a FILE so Directory.CreateDirectory throws on every attempt.
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            string badPath = Path.Combine(blocker, "save.json");

            var log = new FakeLogger();
            using var queue = new PersistenceQueue(log, maxAttempts: 2, retryDelay: TimeSpan.FromMilliseconds(1));
            PersistenceWriteFailedEventArgs? failure = null;
            queue.WriteFailed += (_, e) => failure = e;

            queue.Enqueue(badPath, "data"); // must not throw
            queue.Flush();

            Assert.NotNull(failure);
            Assert.Equal(badPath, failure!.Path);
            Assert.Equal(2, failure.AttemptCount);
            Assert.NotNull(failure.Exception);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteFailed_SubscriberThrows_DoesNotKillWriter()
    {
        string root = NewTempRoot();
        try
        {
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            string badPath = Path.Combine(blocker, "save.json");
            string goodPath = Path.Combine(root, "good.json");

            using var queue = new PersistenceQueue(maxAttempts: 1, retryDelay: TimeSpan.FromMilliseconds(1));
            queue.WriteFailed += (_, _) => throw new InvalidOperationException("subscriber blew up");

            queue.Enqueue(badPath, "data"); // triggers the throwing subscriber on the worker thread
            queue.Enqueue(goodPath, "ok");  // worker must survive and still service this
            queue.Flush();

            Assert.Equal("ok", File.ReadAllText(goodPath));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void Enqueue_AppDataPathsOverload_WritesToResolvedFilePath()
    {
        string root = NewTempRoot();
        try
        {
            var env = new FakeAppDataEnvironment { IsWindows = true };
            env.Folders[Environment.SpecialFolder.ApplicationData] = root;
            var paths = new AppDataPaths("APKiwi", "MyGame", env);

            using var queue = new PersistenceQueue();
            queue.Enqueue(paths, "save.json", "data");
            queue.Flush();

            Assert.Equal("data", File.ReadAllText(paths.GetFilePath("save.json")));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void EnqueueGeneric_SerializesValueAsJson()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(path, new { Score = 42 });
            queue.Flush();

            string json = File.ReadAllText(path);
            Assert.Contains("\"Score\": 42", json);
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
