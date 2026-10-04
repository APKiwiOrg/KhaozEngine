using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.App;
using KhaozEngine.Persistence;
using Xunit;

namespace KhaozEngine.Tests;

public sealed class TrackedPersistenceTests
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
