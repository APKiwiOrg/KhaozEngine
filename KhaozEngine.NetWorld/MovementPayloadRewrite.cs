using System;
using System.IO;

namespace KhaozEngine.NetWorld;

/// <summary>Brings every historical movement payload forward, composing the owner-timer split before the
/// generation-15 speed widening. Ground commitment state defaults to Ballistic and zero for older bodies.</summary>
internal static class MovementPayloadRewrite
{
    internal const int FineSpeedWireGeneration = 15;
    private const int SpeedWireGeneration = 6;
    // Offset after removing the owner timers: velocity(4), grounded(1), swimming(1), epoch(4), climb(1).
    private const int SpeedOffset = 11;

    internal static bool TryWrite(byte[] body, ref int pos, BinaryWriter writer, int fromGeneration,
        int toGeneration, out byte[]? ownerTimers)
    {
        ownerTimers = null;
        int fromLength = BuiltinBlobLayout.MovementPayloadLength(fromGeneration);
        int toLength = BuiltinBlobLayout.MovementPayloadLength(toGeneration);
        if ((long)pos + fromLength > body.Length) return false;
        scoped ReadOnlySpan<byte> payload = body.AsSpan(pos, fromLength);
        Span<byte> withoutTimers = stackalloc byte[64];
        if (MovementOwnerBlobSplit.Applies(fromGeneration, toGeneration))
        {
            int kept = MovementOwnerBlobSplit.RemoveTimers(payload, withoutTimers, out byte[] timers);
            ownerTimers = timers;
            payload = withoutTimers[..kept];
        }

        bool widenSpeed = fromGeneration >= SpeedWireGeneration && fromGeneration < FineSpeedWireGeneration
            && toGeneration >= FineSpeedWireGeneration;
        int written = payload.Length + (widenSpeed ? 1 : 0);
        if (written > toLength) return false;
        if (widenSpeed)
        {
            writer.Write(payload[..SpeedOffset]);
            // Sign extension must happen before multiplication, including corrupt-but-floorable old values.
            writer.Write((short)(unchecked((sbyte)payload[SpeedOffset]) * 64));
            writer.Write(payload[(SpeedOffset + 1)..]);
        }
        else writer.Write(payload);
        for (int pad = written; pad < toLength; pad++) writer.Write((byte)0);
        pos += fromLength;
        return true;
    }
}
