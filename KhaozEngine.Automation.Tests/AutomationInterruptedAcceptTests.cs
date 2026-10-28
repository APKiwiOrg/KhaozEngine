using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Automation;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Faults at the real started listener's accept boundary, without changing process-wide state.</summary>
public class AutomationInterruptedAcceptTests
{
    static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    const string Token = "interrupted-accept-test-token";

    [Fact]
    public async Task InterruptedAcceptResumesAndServesAFreshAuthenticatedClient()
    {
        using var fault = new AcceptFault(SocketFailure(SocketError.Interrupted));
        var log = new Reports();
        using var listener = Listen(fault.Accept, log);
        await fault.Entered.WaitAsync(Deadline);

        fault.Deliver();

        await AssertResumed(listener, fault);
        using var connection = new WireClient(listener.Port);
        await connection.AssertReply(1);
        Assert.Empty(log.Items);
    }

    [Fact]
    public async Task EstablishedClientRemainsUsableAcrossAnInterruptedAccept()
    {
        using var fault = new AcceptFault(SocketFailure(SocketError.Interrupted), failOnAttempt: 2);
        var log = new Reports();
        using var listener = Listen(fault.Accept, log);
        using var connection = new WireClient(listener.Port);
        await connection.AssertReply(1);
        await fault.Entered.WaitAsync(Deadline);

        fault.Deliver();

        await AssertResumed(listener, fault);
        await connection.AssertReply(2);
        Assert.Empty(log.Items);
    }

    [Fact]
    public async Task DispatchFailureClosesOnlyThatClientAndLaterAcceptanceStillWorks()
    {
        var failure = new InvalidOperationException("injected dispatch failure");
        var log = new Reports();
        using var listener = Listen(static socket => socket.AcceptTcpClient(), log,
            request => request.Id == 1
                ? Task.FromException<AutomationReply>(failure)
                : Reply(request));
        using var failed = new WireClient(listener.Port);
        failed.WriteRequest(1);

        Assert.Null(await failed.ReadLine());
        var report = Assert.Single(log.Items);
        Assert.Same(failure, report.Error);
        Assert.Contains("a connection ended", report.Message);
        using var later = new WireClient(listener.Port);
        await later.AssertReply(2);
        Assert.False(listener.AcceptLoopCompletion.IsCompleted);
    }

    [Fact]
    public async Task DisposeUnblocksPendingAcceptAndClosesClientsWithoutReportingAFault()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempts = 0;
        var log = new Reports();
        using var listener = Listen(socket =>
        {
            if (Interlocked.Increment(ref attempts) == 2) pending.TrySetResult();
            return socket.AcceptTcpClient();
        }, log);
        using var connection = new WireClient(listener.Port);
        await connection.AssertReply(1);
        await pending.Task.WaitAsync(Deadline);

        listener.Dispose();

        await listener.AcceptLoopCompletion.WaitAsync(Deadline);
        Assert.Null(await connection.ReadLine());
        Assert.Equal(2, Volatile.Read(ref attempts));
        Assert.Empty(log.Items);
    }

    [Fact]
    public async Task InterruptedAcceptDeliveredAfterStopNeitherRetriesNorReportsAFault()
    {
        using var fault = new AcceptFault(SocketFailure(SocketError.Interrupted));
        var log = new Reports();
        using var listener = Listen(fault.Accept, log);
        await fault.Entered.WaitAsync(Deadline);

        listener.Dispose();
        fault.Deliver();

        await listener.AcceptLoopCompletion.WaitAsync(Deadline);
        Assert.Equal(1, fault.Attempts);
        Assert.False(fault.Resumed.IsCompleted);
        Assert.Empty(log.Items);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenuineAcceptFailureIsReportedAndTerminatesAcceptance(bool unexpected)
    {
        Exception failure = TerminalFailure(unexpected);
        using var fault = new AcceptFault(failure);
        var log = new Reports();
        using var listener = Listen(fault.Accept, log);
        await fault.Entered.WaitAsync(Deadline);

        fault.Deliver();

        await listener.AcceptLoopCompletion.WaitAsync(Deadline);
        Assert.Equal(1, fault.Attempts);
        Assert.False(fault.Resumed.IsCompleted);
        var report = Assert.Single(log.Items);
        Assert.Same(failure, report.Error);
        Assert.Contains("the accept loop stopped", report.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalAcceptFailurePreservesEstablishedClientEvenWhenLogThrows(bool unexpected)
    {
        Exception failure = TerminalFailure(unexpected);
        using var fault = new AcceptFault(failure, failOnAttempt: 2);
        var log = new Reports(throwOnReport: true);
        using var listener = Listen(fault.Accept, log);
        using var connection = new WireClient(listener.Port);
        await connection.AssertReply(1);
        await fault.Entered.WaitAsync(Deadline);

        fault.Deliver();

        await listener.AcceptLoopCompletion.WaitAsync(Deadline);
        Assert.Equal(2, fault.Attempts);
        Assert.Same(failure, Assert.Single(log.Items).Error);
        await connection.AssertReply(2);

        listener.Dispose();
        Assert.Null(await connection.ReadLine());
    }

    static AutomationListener Listen(
        Func<TcpListener, TcpClient> accept, Reports log,
        Func<AutomationRequest, Task<AutomationReply>>? dispatch = null) =>
        new(new AutomationOptions(true, "")
        {
            Log = log.Write,
            FirstLineTimeout = TimeSpan.Zero,
            IdleTimeout = TimeSpan.Zero,
        }, Token, () => 41, dispatch ?? Reply, accept);

    static Task<AutomationReply> Reply(AutomationRequest request) =>
        Task.FromResult(AutomationReply.Success(request.Id, 41, null));

    static Exception TerminalFailure(bool unexpected) => unexpected
        ? new InvalidOperationException("injected unexpected accept failure")
        : SocketFailure(SocketError.ConnectionAborted);

    static SocketException SocketFailure(SocketError error)
    {
        var failure = new SocketException((int)error);
        // The enum is portable. A historical native errno such as 4 is not this constructor's contract.
        Assert.Equal(error, failure.SocketErrorCode);
        return failure;
    }

    static async Task AssertResumed(AutomationListener listener, AcceptFault fault)
    {
        Task observed = await Task.WhenAny(fault.Resumed, listener.AcceptLoopCompletion).WaitAsync(Deadline);
        Assert.True(observed == fault.Resumed, "The accept loop terminated instead of resuming after Interrupted.");
        Assert.False(listener.AcceptLoopCompletion.IsCompleted);
    }

    /// <summary>Hold a specific accept until the test has established the causal ordering of stop or client use.</summary>
    sealed class AcceptFault(Exception failure, int failOnAttempt = 1) : IDisposable
    {
        readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource _resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _attempts;

        public Task Entered => _entered.Task;
        public Task Resumed => _resumed.Task;
        public int Attempts => Volatile.Read(ref _attempts);

        public TcpClient Accept(TcpListener listener)
        {
            int attempt = Interlocked.Increment(ref _attempts);
            if (attempt == failOnAttempt)
            {
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
                throw failure;
            }
            if (attempt > failOnAttempt) _resumed.TrySetResult();
            return listener.AcceptTcpClient();
        }

        public void Deliver() => _release.TrySetResult();
        public void Dispose() => Deliver();
    }

    sealed class Reports(bool throwOnReport = false)
    {
        public ConcurrentQueue<(string Message, Exception? Error)> Items { get; } = new();

        public void Write(string message, Exception? error)
        {
            Items.Enqueue((message, error));
            if (throwOnReport) throw new InvalidOperationException("injected log failure");
        }
    }

    sealed class WireClient : IDisposable
    {
        readonly TcpClient _client = new();
        readonly StreamReader _reader;
        readonly StreamWriter _writer;

        public WireClient(int port)
        {
            _client.Connect(IPAddress.Loopback, port);
            NetworkStream stream = _client.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public void WriteRequest(long id) =>
            _writer.WriteLine("{\"id\":" + id + ",\"token\":\"" + Token + "\",\"cmd\":\"ping\"}");

        public Task<string?> ReadLine() => _reader.ReadLineAsync().WaitAsync(Deadline);

        public async Task AssertReply(long id)
        {
            WriteRequest(id);
            string? line = await ReadLine();
            Assert.NotNull(line);
            using JsonDocument reply = JsonDocument.Parse(line);
            Assert.Equal(id, reply.RootElement.GetProperty("id").GetInt64());
            Assert.Equal(41, reply.RootElement.GetProperty("frame").GetInt64());
            Assert.True(reply.RootElement.TryGetProperty("ok", out _));
            Assert.False(reply.RootElement.TryGetProperty("error", out _));
        }

        public void Dispose()
        {
            _client.Dispose();
            _reader.Dispose();
            _writer.Dispose();
        }
    }
}
