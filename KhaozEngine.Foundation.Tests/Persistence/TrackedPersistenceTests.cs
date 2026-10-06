using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests;

public sealed class TrackedPersistenceTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Tracked_write_reports_saved_after_atomic_write()
    {
        using var files = new TestFiles();
        using var gate = new WriteGate("payload");
        using var queue = NewQueue(gate);
        Task<PersistenceWriteResult> task = queue.EnqueueTracked(files.Path, "payload");
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            Assert.False(task.IsCompleted);
            Assert.False(File.Exists(files.Path));
        }
        finally { gate.Release.Set(); }

        PersistenceWriteResult result = await task.WaitAsync(Timeout);
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
        using var gate = new WriteGate("hold");
        using var queue = NewQueue(gate);
        queue.Enqueue(files.OtherPath, "hold");
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            Task<PersistenceWriteResult> firstTask = queue.EnqueueTracked(files.Path, "first");
            const string latestPayload = "latest";
            Task<PersistenceWriteResult> latestTask = queue.EnqueueTracked(files.Path, latestPayload);
            PersistenceWriteResult firstResult = await firstTask.WaitAsync(Timeout);
            Assert.Equal(PersistenceWriteOutcome.Superseded, firstResult.Outcome);
            Assert.Equal(files.Path, firstResult.Path);
            Assert.Null(firstResult.Error);
            Assert.False(latestTask.IsCompleted);
            Assert.False(File.Exists(files.Path));
            gate.Release.Set();

            PersistenceWriteResult latestResult = await latestTask.WaitAsync(Timeout);
            Assert.Equal(PersistenceWriteOutcome.Saved, latestResult.Outcome);
            Assert.Equal(latestPayload, File.ReadAllText(files.Path));
            Assert.False(File.Exists(files.Path + ".bak1"));
        }
        finally { gate.Release.Set(); }
    }

    [Fact]
    public async Task Void_enqueue_supersedes_a_pending_tracked_payload()
    {
        using var files = new TestFiles();
        using var gate = new WriteGate("hold");
        using var queue = NewQueue(gate);
        queue.Enqueue(files.OtherPath, "hold");
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            Task<PersistenceWriteResult> task = queue.EnqueueTracked(files.Path, "tracked");
            queue.Enqueue(files.Path, "untracked");
            Assert.Equal(PersistenceWriteOutcome.Superseded, (await task.WaitAsync(Timeout)).Outcome);
        }
        finally { gate.Release.Set(); }

        queue.Flush();
        Assert.Equal("untracked", File.ReadAllText(files.Path));
    }

    [Fact]
    public async Task Older_completion_does_not_complete_newer_request()
    {
        using var files = new TestFiles();
        using var older = new WriteGate("older");
        using var latest = new WriteGate("latest");
        using var queue = NewQueue(older, latest);
        Task<PersistenceWriteResult> olderTask = queue.EnqueueTracked(files.Path, "older");
        try
        {
            await older.Entered.Task.WaitAsync(Timeout);
            Task<PersistenceWriteResult> latestTask = queue.EnqueueTracked(files.Path, "latest");
            older.Release.Set();
            await latest.Entered.Task.WaitAsync(Timeout);
            Assert.Equal(PersistenceWriteOutcome.Saved, (await olderTask.WaitAsync(Timeout)).Outcome);
            Assert.False(latestTask.IsCompleted);
            Assert.Equal("older", File.ReadAllText(files.Path));
            latest.Release.Set();

            Assert.Equal(PersistenceWriteOutcome.Saved, (await latestTask.WaitAsync(Timeout)).Outcome);
            Assert.Equal("latest", File.ReadAllText(files.Path));
            Assert.Equal("older", File.ReadAllText(files.Path + ".bak1"));
        }
        finally
        {
            older.Release.Set();
            latest.Release.Set();
        }
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
        using var gate = new WriteGate("payload");
        using var queue = NewQueue(gate);
        Task<PersistenceWriteResult> task = queue.EnqueueTracked(files.Path, "payload");
        try
        {
            await gate.Entered.Task.WaitAsync(Timeout);
            Assert.False(task.IsCompleted);
        }
        finally { gate.Release.Set(); }

        await Task.Run(() => { if (dispose) queue.Dispose(); else queue.Flush(); }).WaitAsync(Timeout);
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
        using var gate = new WriteGate("payload");
        var queue = NewQueue(gate);
        try
        {
            Task<PersistenceWriteResult> task = queue.EnqueueTracked(files.Path, "payload");
            await gate.Entered.Task.WaitAsync(Timeout);
            Task continuation = task.ContinueWith(_ =>
            {
                queue.Enqueue(files.OtherPath, "followup");
                queue.Flush();
                queue.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            gate.Release.Set();

            await continuation.WaitAsync(Timeout);
            Assert.Equal("payload", File.ReadAllText(files.Path));
            Assert.Equal("followup", File.ReadAllText(files.OtherPath));
            Assert.Throws<ObjectDisposedException>(() => { _ = queue.EnqueueTracked(files.Path, "later"); });
        }
        finally
        {
            gate.Release.Set();
            await Task.Run(queue.Dispose).WaitAsync(Timeout);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Drain_resolves_supersession_even_when_the_replacing_producer_is_paused(bool dispose, bool trackedReplacement)
    {
        using var files = new TestFiles();
        // Diagnostics for #1317. The trace, traced gates and runner add evidence and keep the body's first
        // failure without changing the scenario's gates, Task.Run scheduling, watchdogs or assertions.
        var trace = new TrackedDrainTrace();
        trace.Record("scenario", $"dispose={dispose} trackedReplacement={trackedReplacement}");
        using var writer = new TrackedDrainGate("writer", Timeout, "The controlled writer was not released.", trace);
        using var supersession = new TrackedDrainGate("supersession", Timeout, "The controlled writer was not released.", trace);
        using var continueDrain = new TrackedDrainGate("drain-decision", Timeout, "The drain decision was not released.", trace);
        string invalidChildPath = System.IO.Path.Combine(files.OtherPath, "fail.json");
        var writesDrained = new TrackedDrainFailureSignal(trace, invalidChildPath, new Dictionary<string, string>
        {
            [files.OtherPath] = "hold",
            [files.Path] = "tracked-target",
            [invalidChildPath] = "invalid-child",
        });
        var drainDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new PersistenceQueue((path, json) => trace.Guard("write", () =>
        {
            trace.Record("write-enter", $"role={writesDrained.RoleOf(path)} payload={json}");
            if (json == "hold") writer.Hold();
            AtomicJsonWriter.WriteText(path, json);
            trace.Record("write-exit", $"role={writesDrained.RoleOf(path)} payload={json}");
        }), maxAttempts: 1, retryDelay: TimeSpan.Zero,
            beforeSupersessionCompletion: () => trace.Guard("supersession", supersession.Hold),
            observeDrainDecision: waiting => trace.Guard("drain-decision", () =>
            {
                trace.Record("drain-predicate", $"waiting={waiting}");
                drainDecision.TrySetResult(waiting);
                continueDrain.Hold();
            }));
        // Subscribe before the held write starts, so its watchdog failure is also recorded.
        queue.WriteFailed += writesDrained.Observe;
        Task? producer = null;
        Task? drain = null;
        Task<PersistenceWriteResult>? firstTask = null;
        Task<PersistenceWriteResult>? latestTask = null;
        void RecordTasks(string phase) => trace.Record("tasks",
            $"{phase} producer={producer?.Status} drain={drain?.Status} first={firstTask?.Status} latest={latestTask?.Status}");

        await TrackedDrainRunner.RunAsync(trace, async () =>
        {
            queue.Enqueue(files.OtherPath, "hold");
            trace.Record("body", "await writer entry");
            await writer.Entered.Task.WaitAsync(Timeout);
            Task<PersistenceWriteResult> first = queue.EnqueueTracked(files.Path, "first");
            firstTask = first;
            // The real failed write delivers WriteFailed only after the drain latch is clear. Only the
            // deliberately invalid child path counts as that signal.
            queue.Enqueue(invalidChildPath, "failure");
            trace.Record("producer", "scheduled");
            producer = Task.Run(() => trace.Guard("producer", () =>
            {
                trace.Record("producer", "entered");
                if (trackedReplacement) latestTask = queue.EnqueueTracked(files.Path, "latest");
                else queue.Enqueue(files.Path, "latest");
                trace.Record("producer", "returned");
            }));
            trace.Record("body", "await supersession entry");
            await supersession.Entered.Task.WaitAsync(Timeout);
            writer.Release("body");
            trace.Record("body", "await invalid child WriteFailed");
            await writesDrained.Expected.WaitAsync(Timeout);
            RecordTasks("writes-drained");
            Assert.Equal("latest", File.ReadAllText(files.Path));
            Assert.False(first.IsCompleted);

            trace.Record("drain", "scheduled");
            drain = Task.Run(() => trace.Guard("drain", () =>
            {
                trace.Record("drain", "entered");
                if (dispose) queue.Dispose();
                else queue.Flush();
                trace.Record("drain", "returned");
                Assert.True(first.IsCompletedSuccessfully, "Drain returned with an unresolved superseded request.");
            }));
            // Observe the actual drain predicate outside the lock. A wait decision releases the
            // producer before Flush rechecks, proving a completion pulse cannot be lost. A return
            // decision keeps the producer paused so the real terminal-task assertion catches it.
            trace.Record("body", "await drain decision");
            if (await drainDecision.Task.WaitAsync(Timeout))
            {
                supersession.Release("body");
                trace.Record("body", "await producer after supersession release");
                await producer.WaitAsync(Timeout);
                Assert.True(first.IsCompletedSuccessfully);
            }
            continueDrain.Release("body");
            trace.Record("body", "await drain");
            await drain.WaitAsync(Timeout);
            trace.Record("body", "await producer");
            await producer.WaitAsync(Timeout);
            RecordTasks("drained");

            Assert.Equal(PersistenceWriteOutcome.Superseded, (await first).Outcome);
            if (latestTask is not null)
            {
                Assert.Equal(PersistenceWriteOutcome.Saved, (await latestTask).Outcome);
            }
            writesDrained.AssertNoUnexpected();
        },
        [
            new("release-writer", () => { writer.Release("cleanup"); return Task.CompletedTask; }),
            new("release-supersession", () => { supersession.Release("cleanup"); return Task.CompletedTask; }),
            new("release-drain-decision", () => { continueDrain.Release("cleanup"); return Task.CompletedTask; }),
            new("task-states", () => { RecordTasks("cleanup"); return Task.CompletedTask; }),
            new("producer", () => producer?.WaitAsync(Timeout) ?? Task.CompletedTask),
            new("drain", () => drain?.WaitAsync(Timeout) ?? Task.CompletedTask),
            new("dispose", () => Task.Run(() => trace.Guard("dispose", () =>
            {
                trace.Record("dispose", "entered");
                queue.Dispose();
                trace.Record("dispose", "returned");
            })).WaitAsync(Timeout)),
        ], output.WriteLine);
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

    private static PersistenceQueue NewQueue(WriteGate first, WriteGate? second = null)
        => new((path, json) =>
        {
            first.BeforeWrite(json);
            second?.BeforeWrite(json);
            AtomicJsonWriter.WriteText(path, json);
        }, maxAttempts: 1, retryDelay: TimeSpan.Zero);

    private sealed class WriteGate(string payload) : IDisposable
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(false);

        public void BeforeWrite(string json)
        {
            if (json != payload) return;
            Entered.TrySetResult(true);
            if (!Release.Wait(Timeout)) throw new TimeoutException("The controlled writer was not released.");
        }

        public void Dispose() => Release.Dispose();
    }

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
