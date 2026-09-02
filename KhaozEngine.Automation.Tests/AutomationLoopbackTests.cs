using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using KhaozEngine.Automation;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>
/// A real socket round trip against the fake pump: connect over loopback, send a line, read the reply. Gate 3's
/// token behaviour is asserted here rather than in the protocol tests because closing the connection IS the
/// behaviour, and only a real connection can show it.
/// <para>
/// In the <c>AutomationEnvironment</c> collection because starting a host writes <c>KE_AUTOMATION</c>.
/// </para>
/// </summary>
[Collection("AutomationEnvironment")]
public class AutomationLoopbackTests
{
    /// <summary>How long a test client waits for a line before it calls the host broken.</summary>
    static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a test waits for a message the host reports from a socket thread.</summary>
    static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(10);


    [Fact]
    public void PingWithTheTokenRoundTripsOverLoopback()
    {
        using var session = new HostSession();
        using Connection connection = session.Connect();

        JsonElement reply = connection.Send("{\"id\":1,\"token\":\"" + session.Host.Token + "\",\"cmd\":\"ping\"}");

        Assert.Equal(1, reply.GetProperty("id").GetInt64());
        Assert.Equal(0, reply.GetProperty("frame").GetInt64());
        Assert.True(reply.TryGetProperty("ok", out _));
    }

    [Fact]
    public void TheListenerBindsLoopbackOnly()
    {
        using var session = new HostSession();

        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, session.Host.Port);

        // The dual-stack socket reports the peer as the IPv4-mapped form (::ffff:127.0.0.1), so assert loopback-ness
        // rather than a literal address.
        Assert.True(client.Connected);
        Assert.True(IPAddress.IsLoopback(((IPEndPoint)client.Client.RemoteEndPoint!).Address));
    }

    [Fact]
    public void AQueuedCommandRepliesOnceThePumpRunsIt()
    {
        using var session = new HostSession();
        using Connection connection = session.Connect();
        session.Host.StateProvider = () => new System.Text.Json.Nodes.JsonObject { ["hp"] = 7 };

        connection.Write("{\"id\":2,\"token\":\"" + session.Host.Token + "\",\"cmd\":\"state\"}");
        session.PumpUntilReplied(connection);

        JsonElement reply = connection.Read();
        Assert.Equal(7, reply.GetProperty("ok").GetProperty("hp").GetInt32());
        Assert.True(reply.GetProperty("frame").GetInt64() >= 1);
    }

    [Fact]
    public void AMalformedLineGetsAnErrorAndTheConnectionStaysOpen()
    {
        using var session = new HostSession();
        using Connection connection = session.Connect();

        JsonElement first = connection.Send("this is not json");
        Assert.Contains("malformed JSON", first.GetProperty("error").GetString());

        JsonElement second = connection.Send("{\"id\":5,\"token\":\"" + session.Host.Token + "\",\"cmd\":\"ping\"}");
        Assert.Equal(5, second.GetProperty("id").GetInt64());
        Assert.True(second.TryGetProperty("ok", out _));
    }

    [Fact]
    public void AWrongTokenGetsOneRefusalAndTheConnectionCloses()
    {
        using var session = new HostSession();
        using Connection connection = session.Connect();

        JsonElement reply = connection.Send("{\"id\":6,\"token\":\"not-the-token\",\"cmd\":\"ping\"}");

        Assert.Equal("unauthorized", reply.GetProperty("error").GetString());
        Assert.Equal(6, reply.GetProperty("id").GetInt64());
        Assert.Null(connection.ReadLineOrNull());              // the host closed the connection
    }

    [Fact]
    public void AMissingTokenIsRefusedTheSameWay()
    {
        using var session = new HostSession();
        using Connection connection = session.Connect();

        JsonElement reply = connection.Send("{\"id\":7,\"cmd\":\"ping\"}");

        Assert.Equal("unauthorized", reply.GetProperty("error").GetString());
        Assert.Null(connection.ReadLineOrNull());
    }

    [Fact]
    public void DisposingTheHostUnderAFreshConnectionTakesNothingDownWithIt()
    {
        // The listener's threads run inside a RUNNING GAME, so an escaped exception kills the process. This races
        // Dispose against the accept thread on purpose: it is the window that crashed a test host, because
        // TcpClient.NoDelay on an already-closed client throws NullReferenceException rather than
        // ObjectDisposedException. A crash on a serve thread aborts the whole run, so an assembly that finishes is
        // the assertion.
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var session = new HostSession();
            var client = new TcpClient();
            client.Connect(IPAddress.Loopback, session.Host.Port);
            session.Dispose();
            client.Dispose();
        }

        Assert.True(true);
    }

    [Fact]
    public void ARequestLinePastTheCapIsRefusedOnceAndTheConnectionCloses()
    {
        using var session = new HostSession();
        using Connection connection = session.Connect();

        connection.WriteRaw(new string('a', AutomationHost.MaxRequestLineBytes + 1024));   // no newline, ever

        JsonElement reply = connection.Read();
        Assert.Contains("request line exceeds", reply.GetProperty("error").GetString());
        Assert.Null(connection.ReadLineOrNull());
        Assert.True(session.Log.Wait("request line passed", ReportTimeout), Reported(session));
    }

    [Fact]
    public void AMegabyteWithNoNewlineDoesNotCostTheHostAMegabyteOrItsEndpoint()
    {
        // The measured failure this replaces: 200 MB written with no newline took the managed heap past a gigabyte
        // in under two seconds, with the connection still open and nothing authenticated. The reader caps at
        // AutomationHost.MaxRequestLineBytes, which AutomationLineReaderTests pins by counting the bytes it took.
        using var session = new HostSession();
        string chunk = new('a', 64 * 1024);
        using (Connection flood = session.Connect())
        {
            for (int i = 0; i < 16 && flood.TryWriteRaw(chunk); i++) { }
            Assert.True(session.Log.Wait("request line passed", ReportTimeout), Reported(session));
        }

        using Connection connection = session.Connect();
        JsonElement reply = connection.Send("{\"id\":3,\"token\":\"" + session.Host.Token + "\",\"cmd\":\"ping\"}");
        Assert.True(reply.TryGetProperty("ok", out _));
    }

    [Fact]
    public void AConnectionThatSendsNothingIsClosedWhenItsFirstLineDeadlineExpires()
    {
        using var session = new HostSession(firstLineTimeout: TimeSpan.FromMilliseconds(200));
        using Connection connection = session.Connect();

        Assert.Null(connection.ReadLineOrNull());               // the host closed it without being asked anything
        Assert.True(session.Log.Wait("without a complete request line", ReportTimeout), Reported(session));
    }

    [Fact]
    public void TheAcceptLoopReportsItsOwnDeath()
    {
        // An accept loop that dies for a reason other than shutdown takes the endpoint down for the whole run, and
        // the bridge sees nothing but connection refused. There is no ordinary way to induce that from outside, so
        // this reaches through the host to the accept socket and stops it while the listener still thinks it is
        // running, which is exactly the shape of the failure being reported.
        using var session = new HostSession();

        StopTheAcceptSocketBehindTheListenersBack(session.Host);

        Assert.True(session.Log.Wait("the accept loop stopped", ReportTimeout), Reported(session));
    }

    [Fact]
    public void APendingCommandGetsItsFailureLineBeforeTheSocketCloses()
    {
        using var session = new HostSession();
        using Connection connection = session.Connect();

        connection.Write("{\"id\":11,\"token\":\"" + session.Host.Token + "\",\"cmd\":\"step\",\"frames\":9999}");
        InputState real = AutomationTestKit.Real();
        for (int i = 0; i < 200 && session.Host.WaitingCount == 0; i++)
        {
            session.Host.Pump(real);
            Thread.Sleep(5);
        }
        Assert.Equal(1, session.Host.WaitingCount);

        session.Host.Dispose();

        JsonElement reply = connection.Read();
        Assert.Equal(11, reply.GetProperty("id").GetInt64());
        Assert.Equal("automation host stopped", reply.GetProperty("error").GetString());
        Assert.Null(connection.ReadLineOrNull());
    }

    /// <summary>Everything the host reported, for a failure message worth reading.</summary>
    static string Reported(HostSession session) =>
        "reported: [" + string.Join(" | ", session.Log.Messages) + "]";

    /// <summary>Close the accept socket without telling the listener it is shutting down.</summary>
    static void StopTheAcceptSocketBehindTheListenersBack(AutomationHost host)
    {
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        object listener = typeof(AutomationHost).GetField("_listener", Private)!.GetValue(host)!;
        var socket = (TcpListener)listener.GetType().GetField("_listener", Private)!.GetValue(listener)!;
        socket.Stop();
    }

    /// <summary>A started host plus the environment and temp directory it needs, all torn down together.</summary>
    sealed class HostSession : IDisposable
    {
        readonly TempDirectory _temp = new();
        readonly EnvironmentScope _environment;

        public AutomationHost Host { get; }

        /// <summary>What the host reported through its <see cref="AutomationOptions.Log"/> hook.</summary>
        public LogSink Log { get; } = new();

        public HostSession(TimeSpan? firstLineTimeout = null, TimeSpan? idleTimeout = null)
        {
            _environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, "1");
            var options = new AutomationOptions(Enabled: true, _temp.Path) { Log = Log.Write };
            if (firstLineTimeout is TimeSpan first) options = options with { FirstLineTimeout = first };
            if (idleTimeout is TimeSpan idle) options = options with { IdleTimeout = idle };
            Host = new AutomationHost(options);
            Host.Start();
        }

        public Connection Connect() => new(Host.Port);

        /// <summary>Pump frames until the connection has a reply waiting, standing in for the window thread.</summary>
        public void PumpUntilReplied(Connection connection)
        {
            InputState real = AutomationTestKit.Real();
            for (int i = 0; i < 200 && !connection.HasData; i++)
            {
                Host.Pump(real);
                Thread.Sleep(5);
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            _environment.Dispose();
            _temp.Dispose();
        }
    }

    /// <summary>One loopback connection, line in and line out.</summary>
    sealed class Connection : IDisposable
    {
        readonly TcpClient _client;
        readonly StreamReader _reader;
        readonly StreamWriter _writer;

        public Connection(int port)
        {
            _client = new TcpClient();
            _client.Connect(IPAddress.Loopback, port);
            _client.NoDelay = true;
            // Without a deadline a regression that stops the host replying or closing HANGS the run instead of
            // failing it, which is the difference between a red test and a stuck CI leg.
            _client.ReceiveTimeout = (int)ReadTimeout.TotalMilliseconds;
            NetworkStream stream = _client.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public bool HasData => _client.Available > 0;

        public void Write(string line) => _writer.WriteLine(line);

        /// <summary>Write with no newline behind it, which is how a caller builds a line that never ends.</summary>
        public void WriteRaw(string text) => _writer.Write(text);

        /// <summary>Write with no newline, tolerating the host having already closed on us.</summary>
        public bool TryWriteRaw(string text)
        {
            try
            {
                _writer.Write(text);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public JsonElement Read() => AutomationTestKit.Json(_reader.ReadLine() ?? "{}");

        public string? ReadLineOrNull() => _reader.ReadLine();

        public JsonElement Send(string line)
        {
            Write(line);
            return Read();
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _client.Dispose();
        }
    }
}
