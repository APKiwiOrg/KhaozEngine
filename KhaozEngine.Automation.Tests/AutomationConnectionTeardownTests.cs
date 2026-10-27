using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.Automation;
using Xunit;

namespace KhaozEngine.Tests;

public class AutomationConnectionTeardownTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchFailureIsReportedBeforeConnectionCloses(bool throwOnReport)
    {
        var deadline = TimeSpan.FromSeconds(10);
        var failure = new InvalidOperationException("injected dispatch failure");
        var accepted = new TaskCompletionSource<Socket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reported = new TaskCompletionSource<(string Message, Exception? Error, bool SocketClosed)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new AutomationListener(new AutomationOptions(true, "")
        {
            FirstLineTimeout = TimeSpan.Zero,
            IdleTimeout = TimeSpan.Zero,
            Log = (message, error) =>
            {
                Socket socket = accepted.Task.GetAwaiter().GetResult();
                // Snapshot before teardown can overtake the test continuation.
                reported.TrySetResult((message, error, socket.SafeHandle.IsClosed));
                if (throwOnReport) throw new InvalidOperationException("injected log failure");
            },
        }, "teardown-test-token", () => 41, _ => Task.FromException<AutomationReply>(failure), socket =>
        {
            TcpClient client = socket.AcceptTcpClient();
            accepted.SetResult(client.Client);
            return client;
        });
        using var peer = new TcpClient();
        await peer.ConnectAsync(IPAddress.Loopback, listener.Port);
        NetworkStream stream = peer.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await writer.WriteLineAsync("{\"id\":1,\"token\":\"teardown-test-token\",\"cmd\":\"ping\"}");

        var report = await reported.Task.WaitAsync(deadline);
        Assert.Same(failure, report.Error);
        Assert.Contains("a connection ended", report.Message);
        Assert.False(report.SocketClosed, "The server socket closed before the connection error was reported.");
        Assert.Null(await reader.ReadLineAsync().WaitAsync(deadline));
        Assert.False(listener.AcceptLoopCompletion.IsCompleted);
    }
}
