using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

// Controlled scenarios hold the queue's drain with a DedicatedDrainScheduler and observe in-flight
// state from inside the write callback, so no pause waits on the test body or a thread-pool thread.
// Timeouts here bound awaits on test-owned threads. They are never a hold's release path.
public sealed class TrackedPersistenceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Tracked_write_reports_saved_after_atomic_write()
    {
        using var files = new TestFiles();
        var drains = new DedicatedDrainScheduler();
        Task<PersistenceWriteResult>? task = null;
        bool completedAtWriteStart = true, existedAtWriteStart = true, completedAtWriteEnd = true;
        using var queue = NewQueue(drains, (path, json) =>
        {
            completedAtWriteStart = task!.IsCompleted;
            existedAtWriteStart = File.Exists(path);
            AtomicJsonWriter.WriteText(path, json);
            completedAtWriteEnd = task.IsCompleted;
        });
        task = queue.EnqueueTracked(files.Path, "payload");
        drains.Start();

        PersistenceWriteResult result = await task.WaitAsync(Timeout);
        Assert.False(completedAtWriteStart);
        Assert.False(existedAtWriteStart);
        Assert.False(completedAtWriteEnd);
        Assert.Equal(PersistenceWriteOutcome.Saved, result.Outcome);
        Assert.Equal(files.Path, result.Path);
        Assert.Null(result.Error);
        Assert.Equal("payload", File.ReadAllText(files.Path));
        Assert.False(File.Exists(files.Path + ".tmp"));
    }

    [Fact]
    public async Task Superseded_payload_is_not_reported_saved()
    {
        using var files = new TestFiles();
        var drains = new DedicatedDrainScheduler();
        using var queue = NewQueue(drains);
        try
        {
            Task<PersistenceWriteResult> firstTask = queue.EnqueueTracked(files.Path, "first");
            const string latestPayload = "latest";
            Task<PersistenceWriteResult> latestTask = queue.EnqueueTracked(files.Path, latestPayload);
            PersistenceWriteResult firstResult = await firstTask.WaitAsync(Timeout);
            Assert.Equal(PersistenceWriteOutcome.Superseded, firstResult.Outcome);
            Assert.Equal(files.Path, firstResult.Path);
            Assert.Null(firstResult.Error);
            Assert.False(latestTask.IsCompleted);
            Assert.False(File.Exists(files.Path));
            drains.Start();

            PersistenceWriteResult latestResult = await latestTask.WaitAsync(Timeout);
            Assert.Equal(PersistenceWriteOutcome.Saved, latestResult.Outcome);
            Assert.Equal(latestPayload, File.ReadAllText(files.Path));
            Assert.False(File.Exists(files.Path + ".bak1"));
        }
        finally { drains.Start(); }
    }

    [Fact]
    public async Task Void_enqueue_supersedes_a_pending_tracked_payload()
    {
        using var files = new TestFiles();
        var drains = new DedicatedDrainScheduler();
        using var queue = NewQueue(drains);
        try
        {
            Task<PersistenceWriteResult> task = queue.EnqueueTracked(files.Path, "tracked");
            queue.Enqueue(files.Path, "untracked");
            Assert.Equal(PersistenceWriteOutcome.Superseded, (await task.WaitAsync(Timeout)).Outcome);
        }
        finally { drains.Start(); }

        await DedicatedThread.Run(queue.Flush).WaitAsync(Timeout);
        Assert.Equal("untracked", File.ReadAllText(files.Path));
    }

    [Fact]
    public async Task Older_completion_does_not_complete_newer_request()
    {
        using var files = new TestFiles();
        var drains = new DedicatedDrainScheduler();
        PersistenceQueue? self = null;
        Task<PersistenceWriteResult>? olderTask = null;
        Task<PersistenceWriteResult>? latestTask = null;
        bool olderCompletedAtLatestWrite = false;
        bool latestCompletedAtItsWrite = true;
        string? onDiskAtLatestWrite = null;
        using var queue = NewQueue(drains, (path, json) =>
        {
            if (json == "older")
            {
                // The newer request lands while the older payload is in flight.
                latestTask = self!.EnqueueTracked(path, "latest");
            }
            else if (json == "latest")
            {
                olderCompletedAtLatestWrite = olderTask!.IsCompletedSuccessfully;
                latestCompletedAtItsWrite = latestTask!.IsCompleted;
                onDiskAtLatestWrite = File.ReadAllText(path);
            }

            AtomicJsonWriter.WriteText(path, json);
        });
        self = queue;
        olderTask = queue.EnqueueTracked(files.Path, "older");
        drains.Start();

        Assert.Equal(PersistenceWriteOutcome.Saved, (await olderTask.WaitAsync(Timeout)).Outcome);
        Assert.NotNull(latestTask);
        Assert.Equal(PersistenceWriteOutcome.Saved, (await latestTask.WaitAsync(Timeout)).Outcome);
        Assert.True(olderCompletedAtLatestWrite);
        Assert.False(latestCompletedAtItsWrite);
        Assert.Equal("older", onDiskAtLatestWrite);
        Assert.Equal("latest", File.ReadAllText(files.Path));
        Assert.Equal("older", File.ReadAllText(files.Path + ".bak1"));
    }

    [Fact]
    public async Task Exhausted_failure_reports_the_error_and_preserves_WriteFailed()
    {
        using var files = new TestFiles();
        File.WriteAllText(files.Path, "blocker");
        string badPath = System.IO.Path.Combine(files.Path, "save.json");
        using var queue = new PersistenceQueue(maxAttempts: 2, retryDelay: TimeSpan.Zero);
        var notification = new TaskCompletionSource<PersistenceWriteFailedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.WriteFailed += (_, args) => notification.TrySetResult(args);

        PersistenceWriteResult result = await queue.EnqueueTracked(badPath, "payload").WaitAsync(Timeout);
        PersistenceWriteFailedEventArgs failure = await notification.Task.WaitAsync(Timeout);
        Assert.Equal(PersistenceWriteOutcome.Failed, result.Outcome);
        Assert.Equal(badPath, result.Path);
        Assert.NotNull(result.Error);
        Assert.Same(failure.Exception, result.Error);
        Assert.Equal(2, failure.AttemptCount);
        Assert.Equal(badPath, failure.Path);
        Assert.Equal("blocker", File.ReadAllText(files.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Flush_and_disposal_drain_tracked_tasks(bool dispose)
    {
        using var files = new TestFiles();
        var drains = new DedicatedDrainScheduler();
        bool? firstDecisionWaited = null;
        // The drain starts only once Flush has decided to wait, so Flush is the call that drains it.
        using var queue = new PersistenceQueue(AtomicJsonWriter.WriteText, maxAttempts: 1, retryDelay: TimeSpan.Zero,
            observeDrainDecision: waiting =>
            {
                firstDecisionWaited ??= waiting;
                drains.Start();
            },
            scheduleDrain: drains.Schedule);
        Task<PersistenceWriteResult> task = queue.EnqueueTracked(files.Path, "payload");
        Assert.False(task.IsCompleted);

        await DedicatedThread.Run(() => { if (dispose) queue.Dispose(); else queue.Flush(); }).WaitAsync(Timeout);
        Assert.True(firstDecisionWaited);
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(PersistenceWriteOutcome.Saved, (await task).Outcome);
        Assert.Equal("payload", File.ReadAllText(files.Path));
        if (dispose)
        {
            Assert.Throws<ObjectDisposedException>(() => { _ = queue.EnqueueTracked(files.Path, "later"); });
        }
    }

    [Fact]
    public async Task Completion_handler_can_enqueue_flush_and_dispose()
    {
        using var files = new TestFiles();
        var drains = new DedicatedDrainScheduler();
        var queue = NewQueue(drains);
        try
        {
            Task<PersistenceWriteResult> task = queue.EnqueueTracked(files.Path, "payload");
            // Attached while the drain is held, so it runs as the completion's continuation.
            Task continuation = task.ContinueWith(_ =>
            {
                queue.Enqueue(files.OtherPath, "followup");
                queue.Flush();
                queue.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            Assert.False(task.IsCompleted);
            drains.Start();

            await continuation.WaitAsync(Timeout);
            Assert.Equal(PersistenceWriteOutcome.Saved, (await task).Outcome);
            Assert.Equal("payload", File.ReadAllText(files.Path));
            Assert.Equal("followup", File.ReadAllText(files.OtherPath));
            Assert.Throws<ObjectDisposedException>(() => { _ = queue.EnqueueTracked(files.Path, "later"); });
        }
        finally
        {
            drains.Start();
            await DedicatedThread.Run(queue.Dispose).WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Drain_resolves_supersession_even_when_the_replacing_producer_is_paused(bool dispose, bool trackedReplacement)
    {
        // Every handoff runs synchronously on the thread that reaches it. The paused producer starts
        // the held drain. The deliberate WriteFailed starts the drainer. The drainer's predicate
        // observation releases the producer and waits for it before Flush rechecks under the lock,
        // proving a completion pulse cannot be lost. A return decision leaves the producer paused,
        // so the drainer's terminal-task assertion catches it.
        using var files = new TestFiles();
        var drains = new DedicatedDrainScheduler();
        using var supersession = new ControlledHold("The supersession was not released.", Timeout);
        using var producerReturned = new ManualResetEventSlim(false);
        string blocker = files.OtherPath;
        File.WriteAllText(blocker, "blocker");
        string invalidChildPath = System.IO.Path.Combine(blocker, "fail.json");
        var drainerStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var unexpectedFailures = new ConcurrentQueue<string>();
        Task<PersistenceWriteResult>? firstTask = null;
        Task<PersistenceWriteResult>? latestTask = null;
        string? onDiskWhenWritesDrained = null;
        bool? firstCompletedWhenWritesDrained = null;
        bool? firstDecisionWaited = null;
        bool? firstCompletedAfterProducer = null;
        int decisions = 0;
        PersistenceQueue? self = null;
        var queue = new PersistenceQueue(AtomicJsonWriter.WriteText, maxAttempts: 1, retryDelay: TimeSpan.Zero,
            beforeSupersessionCompletion: () =>
            {
                drains.Start();
                supersession.Hold();
            },
            observeDrainDecision: waiting =>
            {
                if (Interlocked.Increment(ref decisions) != 1) return;
                firstDecisionWaited = waiting;
                if (!waiting) return;
                supersession.Release();
                if (!producerReturned.Wait(Timeout)) throw new TimeoutException("The released producer did not return.");
                firstCompletedAfterProducer = firstTask!.IsCompletedSuccessfully;
            },
            scheduleDrain: drains.Schedule);
        self = queue;
        // The real failed write delivers WriteFailed only after the drain latch is clear. Only the
        // deliberately invalid child path counts as that signal.
        queue.WriteFailed += (_, args) =>
        {
            if (args.Path != invalidChildPath)
            {
                unexpectedFailures.Enqueue($"{args.Path}: {args.Exception.Message}");
                return;
            }

            PersistenceQueue drained = self!;
            Task<PersistenceWriteResult> first = firstTask!;
            onDiskWhenWritesDrained = File.ReadAllText(files.Path);
            firstCompletedWhenWritesDrained = first.IsCompleted;
            drainerStarted.TrySetResult(DedicatedThread.Run(() =>
            {
                if (dispose) drained.Dispose();
                else drained.Flush();
                Assert.True(first.IsCompletedSuccessfully, "Drain returned with an unresolved superseded request.");
            }));
        };
        Task? producer = null;
        try
        {
            firstTask = queue.EnqueueTracked(files.Path, "first");
            queue.Enqueue(invalidChildPath, "failure");
            producer = DedicatedThread.Run(() =>
            {
                try
                {
                    if (trackedReplacement) latestTask = queue.EnqueueTracked(files.Path, "latest");
                    else queue.Enqueue(files.Path, "latest");
                }
                finally { producerReturned.Set(); }
            });

            Task drainer = await drainerStarted.Task.WaitAsync(Timeout);
            await drainer.WaitAsync(Timeout);
            await producer.WaitAsync(Timeout);

            Assert.Empty(unexpectedFailures);
            Assert.Equal("latest", onDiskWhenWritesDrained);
            Assert.False(firstCompletedWhenWritesDrained);
            Assert.True(firstDecisionWaited);
            Assert.True(firstCompletedAfterProducer);
            Assert.Equal(PersistenceWriteOutcome.Superseded, (await firstTask).Outcome);
            if (trackedReplacement)
            {
                Assert.NotNull(latestTask);
                Assert.Equal(PersistenceWriteOutcome.Saved, (await latestTask).Outcome);
            }
        }
        finally
        {
            drains.Start();
            supersession.Release();
            try
            {
                if (producer is not null) await producer.WaitAsync(Timeout);
            }
            finally { await DedicatedThread.Run(queue.Dispose).WaitAsync(Timeout); }
        }
    }

    [Fact]
    public async Task Controlled_hold_refuses_a_thread_pool_thread()
    {
        using var hold = new ControlledHold("unused", Timeout);
        hold.Release();
        await Task.Run(() => Assert.Throws<InvalidOperationException>(hold.Hold)).WaitAsync(Timeout);
        await DedicatedThread.Run(hold.Hold).WaitAsync(Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveTracked_uses_the_save_posture_and_write_options(bool plaintext)
    {
        using var files = new TestFiles();
        var encoder = new SaveEncoder(new byte[] { 1, 2, 3, 4 }, "TRACKED");
        using var storage = new GameStorage(files.Paths, SaveEncoding.Encoded(encoder),
            new GameStorageOptions { GameVersion = "test-build" });
        var options = new SaveWriteOptions { Encode = plaintext ? false : null, Summary = "summary" };
        PersistenceWriteResult result = await storage.SaveTracked("save.json", new GameStorageTests.TestSave { Score = 42 }, options).WaitAsync(Timeout);
        string path = files.Paths.GetFilePath("save.json");
        string bytes = File.ReadAllText(path);

        Assert.Equal(PersistenceWriteOutcome.Saved, result.Outcome);
        Assert.Equal(path, result.Path);
        Assert.Null(result.Error);
        Assert.Equal(!plaintext, encoder.IsEncoded(bytes));
        Assert.Equal(42, storage.Load<GameStorageTests.TestSave>("save.json").Score);
        if (!plaintext)
        {
            SaveDecodeResult decoded = encoder.TryDecode(bytes);
            Assert.Equal("summary", decoded.Metadata!.Summary);
            Assert.Equal("test-build", decoded.Metadata.GameVersion);
        }
    }

    [Fact]
    public async Task SaveTracked_plaintext_default_writes_serialized_bytes()
    {
        using var files = new TestFiles();
        using var storage = new GameStorage(files.Paths, SaveEncoding.Plaintext);
        PersistenceWriteResult result = await storage.SaveTracked("save.json", new GameStorageTests.TestSave { Score = 42 }).WaitAsync(Timeout);

        Assert.Equal(PersistenceWriteOutcome.Saved, result.Outcome);
        Assert.Contains("\"Score\": 42", File.ReadAllText(result.Path));
        Assert.Throws<InvalidOperationException>(() => { _ = storage.SaveTracked("save.json", 42, new SaveWriteOptions { Encode = true }); });
    }

    private static PersistenceQueue NewQueue(DedicatedDrainScheduler drains, Action<string, string>? writeText = null)
        => new(writeText ?? AtomicJsonWriter.WriteText, maxAttempts: 1, retryDelay: TimeSpan.Zero, scheduleDrain: drains.Schedule);

    private sealed class TestFiles : IDisposable
    {
        private readonly string root = Directory.CreateTempSubdirectory("ke-tracked-").FullName;
        public string Path => System.IO.Path.Combine(root, "save.json");
        public string OtherPath => System.IO.Path.Combine(root, "other.json");
        public AppDataPaths Paths { get; }

        public TestFiles()
        {
            var environment = new FakeAppDataEnvironment { IsMacOS = true };
            environment.Folders[Environment.SpecialFolder.ApplicationData] = root;
            Paths = new AppDataPaths("EngineTests", "TrackedSaves", environment);
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }
}
