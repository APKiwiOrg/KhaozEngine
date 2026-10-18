using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using KhaozEngine.Diagnostics;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Poison draining preserves failures and retained messages while making progress past each batch.</summary>
public sealed class PoisonQueueRequeuerTests
{
    const string Id = "01900000000070008000000000000001";

    [Fact]
    public async Task Requeues_poison_items_and_counts_them()
    {
        var poison = new MemoryPoisonQueue();
        poison.Add("first", new ReportWorkItem(ReportWorkKind.CreateIssue, Id));
        poison.Add("second", new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, Id, 2));
        var work = new RecordingQueueSender();
        work.BeforeSend = () => Assert.DoesNotContain("second", poison.Deleted);
        int moved = await new PoisonQueueRequeuer(poison, work).RequeueAsync(default);
        Assert.Equal(2, moved);
        Assert.Equal(new[] { 1, 3 }, work.Messages.Select(text => ReportWorkItemJson.Parse(text).Requeues));
        Assert.Equal(new[] { "first", "second" }, poison.Deleted);
        Assert.Empty(poison.Messages);
    }

    [Fact]
    public async Task Leaves_items_that_reached_three_requeues()
    {
        var poison = new MemoryPoisonQueue();
        poison.Add("capped", new ReportWorkItem(ReportWorkKind.CreateIssue, Id, 3));
        var log = new RecordingLogger();
        var work = new RecordingQueueSender();
        Assert.Equal(0, await new PoisonQueueRequeuer(poison, work, 3, log).RequeueAsync(default));
        Assert.Single(poison.Messages);
        Assert.Empty(poison.Deleted);
        Assert.Empty(work.Messages);
        Assert.Contains("capped", Assert.Single(log.Warnings), StringComparison.Ordinal);
        Assert.DoesNotContain(Id, log.Warnings[0], StringComparison.Ordinal);
        Assert.Equal(new[] { "capped" }, poison.Released);
    }

    [Fact]
    public async Task Drain_MoreThanTwoBatches_RetainedMessagesDoNotStarveLaterWork()
    {
        var poison = new MemoryPoisonQueue();
        for (int i = 0; i < 32; i++)
            poison.Add("capped" + i, new ReportWorkItem(ReportWorkKind.CreateIssue, Id, 3));
        for (int i = 0; i < 40; i++)
            poison.Add("move" + i, new ReportWorkItem(ReportWorkKind.CreateIssue, Id));
        var work = new RecordingQueueSender();
        var log = new RecordingLogger();
        Assert.Equal(40, await new PoisonQueueRequeuer(poison, work, 3, log).RequeueAsync(default));
        Assert.Equal(40, work.Messages.Count);
        Assert.Equal(32, poison.Messages.Count);
        Assert.Equal(32, log.Warnings.Count);
        Assert.Equal(4, poison.Receives);
        Assert.All(poison.Visibility, value => Assert.True(value > TimeSpan.FromHours(1)));
        Assert.Equal(32, poison.Released.Count);
    }

    [Fact]
    public async Task MalformedMessage_StaysAndLogsOnlyItsIdOnce()
    {
        var poison = new MemoryPoisonQueue();
        poison.Messages.Add(new PoisonQueueMessage("bad-id", "receipt", "secret-invalid-json"));
        poison.Add("valid", new ReportWorkItem(ReportWorkKind.CreateIssue, Id));
        var log = new RecordingLogger();
        Assert.Equal(1, await new PoisonQueueRequeuer(poison, new RecordingQueueSender(), 3, log).RequeueAsync(default));
        Assert.Equal("bad-id", Assert.Single(poison.Messages).Id);
        string warning = Assert.Single(log.Warnings);
        Assert.Contains("bad-id", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SendFailure_DoesNotDeletePoisonItem_AndReleasesRetainedMessages()
    {
        var poison = new MemoryPoisonQueue();
        poison.Add("capped", new ReportWorkItem(ReportWorkKind.CreateIssue, Id, 3));
        poison.Add("retry", new ReportWorkItem(ReportWorkKind.CreateIssue, Id));
        poison.Add("untouched", new ReportWorkItem(ReportWorkKind.CreateIssue, Id));
        var work = new RecordingQueueSender { Failure = new InvalidOperationException("send failed") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PoisonQueueRequeuer(poison, work).RequeueAsync(default));
        Assert.Empty(poison.Deleted);
        Assert.Equal(3, poison.Messages.Count);
        Assert.Contains("capped", poison.Released);
        Assert.Contains("retry", poison.Released);
        Assert.Contains("untouched", poison.Released);
    }

    [Fact]
    public async Task CancellationDuringSend_ReleasesEntireBatchWithoutDeleting()
    {
        using var cancel = new CancellationTokenSource();
        var poison = new MemoryPoisonQueue();
        poison.Add("sending", new ReportWorkItem(ReportWorkKind.CreateIssue, Id));
        poison.Add("untouched", new ReportWorkItem(ReportWorkKind.CreateIssue, Id));
        var work = new RecordingQueueSender { BeforeSend = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PoisonQueueRequeuer(poison, work).RequeueAsync(cancel.Token));
        Assert.Single(work.Messages);
        Assert.Empty(poison.Deleted);
        Assert.Equal(new[] { "sending", "untouched" }, poison.Released);
    }

    [Fact]
    public async Task CustomCap_IsHonoured_AndCancellationPropagates()
    {
        var poison = new MemoryPoisonQueue();
        poison.Add("capped", new ReportWorkItem(ReportWorkKind.CreateIssue, Id, 1));
        Assert.Equal(0, await new PoisonQueueRequeuer(poison, new RecordingQueueSender(), 1).RequeueAsync(default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PoisonQueueRequeuer(poison, new RecordingQueueSender())
            .RequeueAsync(new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PoisonQueueRequeuer(poison, new RecordingQueueSender(), -1));
    }

    internal sealed class MemoryPoisonQueue : IPoisonQueue
    {
        readonly HashSet<string> hidden = new(StringComparer.Ordinal);
        public List<PoisonQueueMessage> Messages { get; } = new();
        public List<string> Deleted { get; } = new();
        public List<string> Released { get; } = new();
        public List<TimeSpan> Visibility { get; } = new();
        public int Receives { get; private set; }

        public void Add(string id, ReportWorkItem item)
            => Messages.Add(new PoisonQueueMessage(id, "receipt", ReportWorkItemJson.Serialize(item)));

        public Task<IReadOnlyList<PoisonQueueMessage>> ReceiveAsync(int maxMessages, TimeSpan visibilityTimeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Receives++;
            Visibility.Add(visibilityTimeout);
            PoisonQueueMessage[] batch = Messages.Where(message => !hidden.Contains(message.Id)).Take(maxMessages).ToArray();
            foreach (PoisonQueueMessage message in batch)
                hidden.Add(message.Id);
            return Task.FromResult<IReadOnlyList<PoisonQueueMessage>>(batch);
        }

        public Task DeleteAsync(PoisonQueueMessage message, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Messages.Remove(message);
            Deleted.Add(message.Id);
            hidden.Remove(message.Id);
            return Task.CompletedTask;
        }

        public Task SetVisibilityAsync(PoisonQueueMessage message, TimeSpan visibilityTimeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(TimeSpan.Zero, visibilityTimeout);
            Released.Add(message.Id);
            hidden.Remove(message.Id);
            return Task.CompletedTask;
        }
    }

    internal sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();
        public string Category => "test";
        public bool IsEnabled(LogLevel level) => true;
        public void Log(LogLevel level, string message, Exception? exception = null) => Warnings.Add(message);
        public void Trace(string message, Exception? exception = null) => Log(LogLevel.Trace, message);
        public void Debug(string message, Exception? exception = null) => Log(LogLevel.Debug, message);
        public void Info(string message, Exception? exception = null) => Log(LogLevel.Info, message);
        public void Warn(string message, Exception? exception = null) => Log(LogLevel.Warn, message);
        public void Error(string message, Exception? exception = null) => Log(LogLevel.Error, message);
        public void Fatal(string message, Exception? exception = null) => Log(LogLevel.Fatal, message);
    }
}
