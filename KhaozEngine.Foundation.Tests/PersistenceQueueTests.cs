using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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
            using var notified = new ManualResetEventSlim(false);
            PersistenceWriteFailedEventArgs? failure = null;
            queue.WriteFailed += (_, e) => { failure = e; notified.Set(); };

            queue.Enqueue(badPath, "data"); // must not throw
            queue.Flush();

            // WriteFailed is raised on the drain thread just after the queue latch is released (so a
            // handler can safely call Flush/Dispose, see issue #150), which can land a hair after
            // Flush() returns. Wait for the notification deterministically rather than racing it.
            Assert.True(notified.Wait(TimeSpan.FromSeconds(5)), "WriteFailed was not raised");
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
    public async Task WriteFailed_HandlerCallsFlush_DoesNotDeadlock()
    {
        string root = NewTempRoot();
        try
        {
            // Make the parent path a FILE so every write attempt fails and the handler fires.
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            string badPath = Path.Combine(blocker, "save.json");
            string goodPath = Path.Combine(root, "good.json");

            var queue = new PersistenceQueue(maxAttempts: 1, retryDelay: TimeSpan.FromMilliseconds(1));
            try
            {
                using var handlerReturned = new ManualResetEventSlim(false);
                queue.WriteFailed += (_, _) =>
                {
                    // A re-entrant Flush() from inside the failure handler must not self-deadlock (issue #150).
                    queue.Flush();
                    handlerReturned.Set();
                };

                queue.Enqueue(badPath, "data");

                bool returned = handlerReturned.Wait(TimeSpan.FromSeconds(5));
                Assert.True(returned, "WriteFailed handler calling Flush() deadlocked the drain thread");

                // The queue must not be wedged after a handler-triggered flush: a fresh write still lands.
                queue.Enqueue(goodPath, "ok");
                var flushTask = Task.Run(() => queue.Flush());
                bool flushed = await Task.WhenAny(flushTask, Task.Delay(TimeSpan.FromSeconds(5))) == flushTask;
                Assert.True(flushed, "Flush() after a handler-flush wedged");
                await flushTask;   // observe any exception thrown by Flush()
                Assert.Equal("ok", File.ReadAllText(goodPath));
            }
            finally
            {
                // Dispose deterministically on every path, but through a timeout guard rather than
                // a using: on a regression the queue is wedged and a bare Dispose() here would hang
                // the runner right after the timeout assert above reported the failure.
                var disposeTask = Task.Run(() => queue.Dispose());
                if (await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5))) == disposeTask)
                {
                    await disposeTask;   // observe any exception thrown by Dispose()
                }
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void WriteFailed_HandlerCallsDispose_DoesNotDeadlock()
    {
        string root = NewTempRoot();
        try
        {
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            string badPath = Path.Combine(blocker, "save.json");

            var queue = new PersistenceQueue(maxAttempts: 1, retryDelay: TimeSpan.FromMilliseconds(1));
            using var handlerReturned = new ManualResetEventSlim(false);
            queue.WriteFailed += (_, _) =>
            {
                // Dispose() calls Flush() internally, so it must be safe from within the handler too (issue #150).
                queue.Dispose();
                handlerReturned.Set();
            };

            queue.Enqueue(badPath, "data");

            bool returned = handlerReturned.Wait(TimeSpan.FromSeconds(5));
            Assert.True(returned, "WriteFailed handler calling Dispose() deadlocked the drain thread");
            Assert.Throws<ObjectDisposedException>(() => queue.Enqueue(badPath, "more"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task WriteFailed_SecondFailureDuringHandler_DeliveredSeriallyInOrder()
    {
        string root = NewTempRoot();
        try
        {
            string blocker = Path.Combine(root, "blocker");
            File.WriteAllText(blocker, "x");
            string badA = Path.Combine(blocker, "a.json");
            string badB = Path.Combine(blocker, "b.json");

            var queue = new PersistenceQueue(maxAttempts: 1, retryDelay: TimeSpan.FromMilliseconds(1));
            try
            {
                using var allDelivered = new ManualResetEventSlim(false);
                var delivered = new List<string>();
                int active = 0;
                int overlapped = 0;
                bool bDeliveredDuringA = false;

                queue.WriteFailed += (_, e) =>
                {
                    if (Interlocked.Increment(ref active) > 1)
                    {
                        Interlocked.Exchange(ref overlapped, 1);
                    }

                    lock (delivered)
                    {
                        delivered.Add(e.Path);
                    }

                    if (e.Path == badA)
                    {
                        // From inside the first notification, produce a second failure and wait for its
                        // drain to finish: Flush() returns only once the tail worker has drained badB
                        // and released the latch, so badB's failure is already queued for delivery
                        // while this handler is still running. Serial delivery means it must not have
                        // been raised yet (issue #150 review: no concurrent or reordered handlers).
                        queue.Enqueue(badB, "data-b");
                        queue.Flush();
                        lock (delivered)
                        {
                            bDeliveredDuringA = delivered.Contains(badB);
                        }
                    }
                    else if (e.Path == badB)
                    {
                        allDelivered.Set();
                    }

                    Interlocked.Decrement(ref active);
                };

                queue.Enqueue(badA, "data-a");

                Assert.True(allDelivered.Wait(TimeSpan.FromSeconds(10)), "not every failure notification was delivered");
                Assert.Equal(0, overlapped);
                Assert.False(bDeliveredDuringA, "badB's failure was delivered while badA's handler was still running");
                lock (delivered)
                {
                    Assert.Equal(new[] { badA, badB }, delivered);
                }
            }
            finally
            {
                // Dispose deterministically on every path, but through a timeout guard rather than a using:
                // on a full regression to the pre-#150 code shape the handler's re-entrant Flush() (above)
                // wedges the drain thread, and a bare Dispose() here (which calls Flush()) would hang the
                // runner right after the timeout assert above already reported the failure (issue #210,
                // mirroring the guarded-dispose finally in WriteFailed_HandlerCallsFlush_DoesNotDeadlock).
                var disposeTask = Task.Run(() => queue.Dispose());
                if (await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5))) == disposeTask)
                {
                    await disposeTask;   // observe any exception thrown by Dispose()
                }
            }
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

    [Fact]
    public void BackupGenerations_RotatePerCommittedWrite()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue(backupGenerations: 2);

            queue.Enqueue(path, "a");
            queue.Flush();
            queue.Enqueue(path, "b");
            queue.Flush();
            queue.Enqueue(path, "c");
            queue.Flush();

            Assert.Equal("c", File.ReadAllText(path));
            Assert.Equal("b", File.ReadAllText(path + ".bak1"));
            Assert.Equal("a", File.ReadAllText(path + ".bak2"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BackupGenerationsDefault_RotatesTwoGenerations()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue();

            queue.Enqueue(path, "a");
            queue.Flush();
            queue.Enqueue(path, "b");
            queue.Flush();
            queue.Enqueue(path, "c");
            queue.Flush();

            // Two generations out of the box, matching what the read side probes. The default used to be 0,
            // so a consumer holding the queue directly wrote no backups at all.
            Assert.Equal("c", File.ReadAllText(path));
            Assert.Equal("b", File.ReadAllText(path + ".bak1"));
            Assert.Equal("a", File.ReadAllText(path + ".bak2"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void BackupGenerationsExplicitlyOff_NoBakFiles()
    {
        string root = NewTempRoot();
        try
        {
            string path = Path.Combine(root, "save.json");
            using var queue = new PersistenceQueue(backupGenerations: 0);

            queue.Enqueue(path, "a");
            queue.Flush();
            queue.Enqueue(path, "b");
            queue.Flush();

            Assert.Equal("b", File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bak1"));
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public void DefaultQueue_FeedsTheSettingsRecoveryLadderAfterACorruptPrimary()
    {
        // The whole point of the default: a consumer that skips GameStorage and drives a bare queue plus a
        // bare FileSettingsStorage gets a ladder with something on it. Both sides are built with defaults
        // here, no backup count passed anywhere.
        string root = Path.Combine(Path.GetTempPath(), "ke-persistqueue-ladder-" + Guid.NewGuid().ToString("N"));
        var env = new FakeAppDataEnvironment { IsMacOS = true };
        env.Folders[Environment.SpecialFolder.ApplicationData] = root;
        var paths = new AppDataPaths("APKiwi", "PersistenceQueueLadder", env);
        try
        {
            using var queue = new PersistenceQueue();
            var storage = new FileSettingsStorage(paths, queue);

            storage.SaveSettings(new LadderSettings { Score = 1 });
            queue.Flush();
            storage.SaveSettings(new LadderSettings { Score = 2 });
            queue.Flush();

            string primary = paths.GetFilePath("settings.json");
            Assert.True(File.Exists(primary + ".bak1"));
            File.WriteAllText(primary, "{ this is not json");

            SaveLoadResult<LadderSettings> result = storage.LoadSettingsDetailed<LadderSettings>();

            Assert.Equal(SaveLoadOutcome.RecoveredFromBackup, result.Outcome);
            Assert.Equal(1, result.RecoveredGeneration);
            Assert.Equal(1, result.Value.Score);
        }
        finally { Cleanup(root); }
    }

    private sealed class LadderSettings
    {
        public int Score { get; set; }
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
