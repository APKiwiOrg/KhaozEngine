using System;

namespace KhaozEngine.Netcode;

/// <summary>Session-layer message kind, carried as the first byte of every framed session payload.</summary>
public enum SessionOpcode : byte
{
    /// <summary>Unrecognized / empty frame (defensive default for hostile input).</summary>
    Unknown = 0x00,
    /// <summary>client→server: auth token (body may be empty).</summary>
    Hello = 0x01,
    /// <summary>server→client: accepted; body is the assigned slot (4-byte little-endian int).</summary>
    Welcome = 0x02,
    /// <summary>server→client: rejected; body is the UTF-8 reason.</summary>
    Reject = 0x03,
    /// <summary>both: opaque game payload follows.</summary>
    Data = 0x10
}

/// <summary>Reads/writes the 1-byte-opcode session frame: <c>[opcode][body...]</c>.</summary>
public static class SessionFrame
{
    /// <summary>Allocates <c>[opcode][body]</c>.</summary>
    public static byte[] Write(SessionOpcode opcode, ReadOnlySpan<byte> body)
    {
        var buffer = new byte[1 + body.Length];
        buffer[0] = (byte)opcode;
        body.CopyTo(buffer.AsSpan(1));
        return buffer;
    }

    /// <summary>The opcode, or <see cref="SessionOpcode.Unknown"/> for an empty/unrecognized frame.</summary>
    public static SessionOpcode ReadOpcode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length == 0) return SessionOpcode.Unknown;
        return frame[0] switch
        {
            (byte)SessionOpcode.Hello => SessionOpcode.Hello,
            (byte)SessionOpcode.Welcome => SessionOpcode.Welcome,
            (byte)SessionOpcode.Reject => SessionOpcode.Reject,
            (byte)SessionOpcode.Data => SessionOpcode.Data,
            _ => SessionOpcode.Unknown
        };
    }

    /// <summary>The body after the opcode byte (empty for a 0- or 1-byte frame).</summary>
    public static byte[] ReadBody(ReadOnlySpan<byte> frame) =>
        frame.Length <= 1 ? Array.Empty<byte>() : frame.Slice(1).ToArray();
}
