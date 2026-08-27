using System;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The tile wire. Every frame carries a leading TAG byte, so the demux is by tag and never by LENGTH: this
/// protocol shares no bytes and no framing rules with <c>MoveProtocol</c> and is not interoperable with it, by
/// design (the two movement stacks are siblings, and a tile server never speaks the float protocol). Tagging
/// rather than measuring is what keeps a future frame kind from silently colliding with an existing one that
/// happens to be the same size.
/// <para>Every decoder is total: it returns false for anything malformed, truncated or mis-tagged and never
/// throws, because every byte it reads is attacker-controlled. A decoder that threw would hand a remote peer a
/// way to kill the receiving loop with one bad packet.</para>
/// <para>Partial because the frame families land in their own files. This one is the client command frame.</para>
/// </summary>
public static partial class TileProtocol
{
    /// <summary>Client-to-server tag: a sequenced <see cref="TileCommand"/>.</summary>
    public const byte ClientFrameCommand = 0;

    // [tag:1][seq:4][kind:1][goalX:4][goalZ:4][plane:1][mode:1][target:8] = 24 bytes, fixed.
    const int CommandFrameSize = 1 + 4 + 1 + 4 + 4 + 1 + 1 + 8;

    /// <summary>Encodes one sequenced command into a fixed size frame. <paramref name="seq"/> must be non-negative
    /// (the queue rejects a negative seq on the far side, so encoding one would be a frame that can never be
    /// accepted). The size is fixed rather than kind-dependent so both ends do the same arithmetic on every
    /// frame. The plane rides in ONE byte, so a goal plane outside 0..255 truncates here rather than throwing (the
    /// encoder validates nothing, matching its acceptance of a negative seq), and the truncated plane is caught at
    /// apply, where the server knows which plane the player is actually standing on.</summary>
    public static byte[] EncodeCommand(int seq, in TileCommand command)
    {
        var b = new byte[CommandFrameSize];
        b[0] = ClientFrameCommand;
        BitConverter.TryWriteBytes(b.AsSpan(1, 4), seq);
        b[5] = (byte)command.Kind;
        BitConverter.TryWriteBytes(b.AsSpan(6, 4), command.Goal.X);
        BitConverter.TryWriteBytes(b.AsSpan(10, 4), command.Goal.Z);
        b[14] = (byte)command.Goal.Plane;
        b[15] = (byte)command.Mode;
        BitConverter.TryWriteBytes(b.AsSpan(16, 8), command.Target);
        return b;
    }

    /// <summary>
    /// Decodes a command frame. False (never throws) for: a frame that is not exactly
    /// <see cref="CommandFrameSize"/> bytes, a wrong tag, a negative sequence number, a kind outside
    /// <see cref="TileCommandKind"/>, a mode outside <see cref="TileMoveMode"/>, or a plane outside
    /// <c>0 .. planeCount - 1</c>. The two enum checks matter more than they look: a byte cast into an enum is not
    /// validated by the runtime, so an unchecked one would reach a switch that has no case for it.
    /// <para>The GOAL RADIUS is deliberately not checked here: it is relative to where the player currently stands,
    /// which only the server knows, so it is enforced at apply. This decoder validates SHAPE, and everything that
    /// needs the world's state to judge stays with the caller that has it.</para>
    /// </summary>
    public static bool TryDecodeCommand(ReadOnlySpan<byte> data, int planeCount, out int seq, out TileCommand command)
    {
        seq = -1;
        command = TileCommand.None;
        if (data.Length != CommandFrameSize || data[0] != ClientFrameCommand) return false;

        int s = BitConverter.ToInt32(data.Slice(1, 4));
        if (s < 0) return false;

        byte kind = data[5];
        if (kind > (byte)TileCommandKind.Attack) return false;
        byte mode = data[15];
        if (mode > (byte)TileMoveMode.Run) return false;
        int plane = data[14];
        if (plane >= planeCount) return false;

        seq = s;
        command = new TileCommand(
            (TileCommandKind)kind,
            new TileCoord(BitConverter.ToInt32(data.Slice(6, 4)), BitConverter.ToInt32(data.Slice(10, 4)), plane),
            (TileMoveMode)mode,
            BitConverter.ToInt64(data.Slice(16, 8)));
        return true;
    }
}
