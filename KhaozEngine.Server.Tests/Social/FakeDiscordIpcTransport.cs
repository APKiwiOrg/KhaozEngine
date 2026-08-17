using System;
using System.Collections.Generic;
using KhaozEngine.Social.Discord.Internal;

namespace KhaozEngine.Tests;

/// <summary>In-memory <see cref="IDiscordIpcTransport"/>: captures writes, replays queued reads.</summary>
internal sealed class FakeDiscordIpcTransport : IDiscordIpcTransport
{
    private readonly Queue<byte[]> incoming = new();
    private readonly List<byte> written = new();

    public bool ConnectResult { get; set; } = true;
    public bool IsConnected { get; private set; }
    public int DisposeCalls { get; private set; }
    public int DisconnectCalls { get; private set; }
    public bool ThrowOnWrite { get; set; }

    /// <summary>Throws out of <see cref="Read"/>, the way a socket that broke mid-session does.</summary>
    public bool ThrowOnRead { get; set; }

    public IReadOnlyList<byte> Written => written;

    public bool TryConnect()
    {
        // A fresh connection: the previous session's queued frames are not this one's.
        incoming.Clear();
        IsConnected = ConnectResult;
        return ConnectResult;
    }

    /// <summary>
    /// The player quits Discord. The socket closes with no Close frame and no exception anywhere: the real
    /// transport's reader thread hits end-of-stream and goes !IsConnected, and that is the whole of the
    /// evidence the client gets.
    /// </summary>
    public void SimulateQuietDeath() => IsConnected = false;

    public void Disconnect()
    {
        DisconnectCalls++;
        IsConnected = false;
        incoming.Clear();
    }

    /// <summary>Queue a full frame the next Read(s) will surface.</summary>
    public void EnqueueFrame(DiscordIpcOpcode opcode, string json)
        => incoming.Enqueue(DiscordIpcCodec.EncodeFrame(opcode, json));

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (ThrowOnWrite)
        {
            throw new System.IO.IOException("broken pipe");
        }

        written.AddRange(bytes.ToArray());
    }

    public int Read(Span<byte> buffer)
    {
        if (ThrowOnRead)
        {
            throw new System.IO.IOException("broken pipe");
        }

        if (incoming.Count == 0)
        {
            return 0;
        }

        byte[] next = incoming.Peek();
        if (next.Length > buffer.Length)
        {
            return 0; // caller must pass a big-enough buffer in these tests
        }

        incoming.Dequeue();
        next.CopyTo(buffer);
        return next.Length;
    }

    /// <summary>Decode the last complete frame written so far (writes accumulate: handshake, subscribes, then set-activity).</summary>
    public bool TryReadLastWrittenFrame(out DiscordIpcOpcode opcode, out string json)
    {
        opcode = default;
        json = string.Empty;
        ReadOnlySpan<byte> span = written.ToArray();
        bool any = false;
        while (DiscordIpcCodec.TryDecodeFrame(span, out DiscordIpcOpcode op, out string body, out int consumed))
        {
            opcode = op;
            json = body;
            any = true;
            span = span.Slice(consumed);
        }

        return any;
    }

    public void Dispose() => DisposeCalls++;
}
