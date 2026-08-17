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
    private Stream? stream;
    private Thread? reader;
    private volatile bool running;

    public bool IsConnected => running && stream is not null;

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
                stream = s;
                running = true;
                reader = new Thread(ReadLoop) { IsBackground = true, Name = "discord-ipc-reader" };
                reader.Start();
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

    private void ReadLoop()
    {
        byte[] buffer = new byte[4096];
        Stream? s = stream;
        if (s is null)
        {
            return;
        }

        while (running)
        {
            int read;
            try
            {
                read = s.Read(buffer, 0, buffer.Length);
            }
            catch (Exception)
            {
                break; // stream disposed or broken
            }

            if (read <= 0)
            {
                break; // closed
            }

            bool overflow;
            lock (gate)
            {
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

        running = false;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        Stream? s = stream;
        if (s is null)
        {
            return;
        }

        s.Write(bytes);
        s.Flush();
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

    public void Dispose() => Teardown();

    private void Teardown()
    {
        running = false;
        try { stream?.Dispose(); } catch { /* ignore */ }
        try { reader?.Join(200); } catch { /* ignore */ }
        stream = null;
        reader = null;
        lock (gate)
        {
            pending.Clear();
        }
    }
}
