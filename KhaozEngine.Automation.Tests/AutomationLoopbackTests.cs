using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
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

    /// <summary>A started host plus the environment and temp directory it needs, all torn down together.</summary>
    sealed class HostSession : IDisposable
    {
        readonly TempDirectory _temp = new();
        readonly EnvironmentScope _environment;

        public AutomationHost Host { get; }

        public HostSession()
        {
            _environment = new EnvironmentScope(AutomationHost.EnvironmentVariable, "1");
            Host = new AutomationHost(new AutomationOptions(Enabled: true, _temp.Path));
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
            NetworkStream stream = _client.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        }

        public bool HasData => _client.Available > 0;

        public void Write(string line) => _writer.WriteLine(line);

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
