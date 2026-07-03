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
    public bool ThrowOnWrite { get; set; }

    public IReadOnlyList<byte> Written => written;

    public bool TryConnect()
    {
        IsConnected = ConnectResult;
        return ConnectResult;
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
