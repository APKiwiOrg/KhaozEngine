using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Threading;

namespace KhaozEngine.Social.Discord.Internal;

/// <summary>
/// Real Discord IPC transport. Windows connects to the named pipe <c>discord-ipc-N</c>; macOS/Linux
/// connect to the unix domain socket Discord exposes under a runtime/temp dir (via
/// <see cref="DiscordSocketPaths"/>) - .NET's NamedPipeClientStream on unix maps to
/// <c>/tmp/CoreFxPipe_*</c>, which is NOT Discord's path, so a raw Socket wrapped in a NetworkStream is
/// used there instead. Tries indices 0..9. A background reader thread drains the stream into a buffer so
/// <see cref="Read"/> never blocks the game loop.
/// </summary>
internal sealed class NamedPipeDiscordTransport : IDiscordIpcTransport
{
    // Bounds the read buffer if the consumer stops draining (e.g. the client disconnected but a
    // misbehaving peer keeps sending). Rich-presence frames are tiny, so 8 MiB is far above normal;
    // exceeding it means the connection is broken, so the reader stops rather than pin memory.
    private const int MaxPendingBytes = 8 * 1024 * 1024;

    private readonly object gate = new();
    private readonly List<byte> pending = new();

    // Volatile so the reads are honest rather than cached: the frame thread writes this field and the
    // members that read it are cheap flag checks with no lock of their own. It does not make the class
    // thread-safe, and is not meant to (see Teardown).
    private volatile Connection? current;

    public bool IsConnected => current is { Live: true };

    public bool TryConnect()
    {
        // The controller re-attempts a failed connect on this same instance, so tear down anything a
        // previous attempt left live first. Overwriting the stream instead would leak it AND leave its
        // reader thread appending into the shared buffer, interleaving two connections' bytes.
        Teardown();

        for (int i = 0; i < 10; i++)
        {
            Stream? s = OperatingSystem.IsWindows() ? TryConnectWindows(i) : TryConnectUnix(i);
            if (s is not null)
            {
                var connection = new Connection(s);
                connection.Reader = new Thread(() => ReadLoop(connection))
                {
                    IsBackground = true,
                    Name = "discord-ipc-reader",
                };
                current = connection;
                connection.Reader.Start();
                return true;
            }
        }

        return false;
    }

    private static Stream? TryConnectWindows(int index)
    {
        try
        {
            var client = new NamedPipeClientStream(".", $"discord-ipc-{index}", PipeDirection.InOut, PipeOptions.Asynchronous);
            client.Connect(100);
            return client;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Stream? TryConnectUnix(int index)
    {
        foreach (string path in DiscordSocketPaths.UnixCandidates(index, Environment.GetEnvironmentVariable))
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                socket.Connect(new UnixDomainSocketEndPoint(path));
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception)
            {
                // try the next candidate path
            }
        }

        return null;
    }

    private void ReadLoop(Connection connection)
    {
        byte[] buffer = new byte[4096];

        while (connection.Live)
        {
            int read;
            try
            {
                read = connection.Stream.Read(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                break; // stream disposed or broken
            }

            if (read <= 0)
            {
                break; // closed: Discord went away, and this is the only place that hears about it
            }

            bool overflow;
            lock (gate)
            {
                // Re-check under the gate, because a reader whose Join(200) timed out is still running while
                // the NEXT session drains this same buffer, and its bytes landing there would desync every
                // frame decoded after them. Teardown clears Live BEFORE it takes the gate to clear pending,
                // so a stale reader either appended ahead of that clear (and the clear wiped it) or reads
                // false here and appends nothing.
                if (!connection.Live)
                {
                    break;
                }

                for (int i = 0; i < read; i++)
                {
                    pending.Add(buffer[i]);
                }

                overflow = pending.Count > MaxPendingBytes;
            }

            if (overflow)
            {
                break; // consumer is not draining; stop buffering to bound memory
            }
        }

        // Retire THIS connection, and only it. A reader still unwinding after a reconnect must not be able
        // to mark the connection that replaced it dead, which a flag shared across connections lets it do.
        // Reconnecting mid-session is routine now (#655), so several connections per process is the normal
        // case rather than the exotic one.
        connection.Live = false;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        Connection? connection = current;
        if (connection is null)
        {
            return;
        }

        connection.Stream.Write(bytes);
        connection.Stream.Flush();
    }

    public int Read(Span<byte> buffer)
    {
        lock (gate)
        {
            int count = Math.Min(buffer.Length, pending.Count);
            if (count == 0)
            {
                return 0;
            }

            for (int i = 0; i < count; i++)
            {
                buffer[i] = pending[i];
            }

            pending.RemoveRange(0, count);
            return count;
        }
    }

    public void Disconnect() => Teardown();

    public void Dispose() => Teardown();

    // Idempotent, and driven from the frame thread: every caller is a public member of this class, and the
    // game loop is what calls those, so two teardowns do not race each other. Two things still cross a
    // thread boundary, and both are handled rather than assumed away. The reader thread reads Live and the
    // pending buffer, so Live is volatile and cleared BEFORE the gate is taken, and pending is only ever
    // touched under it. And the reader thread could in principle be the caller, so the join is guarded
    // against a self-join rather than left to throw into a catch that hides it.
    private void Teardown()
    {
        Connection? connection = current;
        current = null;
        if (connection is null)
        {
            return;
        }

        connection.Live = false;
        try { connection.Stream.Dispose(); } catch { /* ignore */ }

        Thread? reader = connection.Reader;
        if (reader is not null && reader != Thread.CurrentThread)
        {
            try { reader.Join(200); } catch { /* ignore */ }
        }

        lock (gate)
        {
            pending.Clear();
        }
    }

    // One connection and the thread draining it, kept together so a reader only ever touches its own.
    private sealed class Connection
    {
        public Connection(Stream stream) => Stream = stream;

        public Stream Stream { get; }

        public Thread? Reader { get; set; }

        public volatile bool Live = true;
    }
}
