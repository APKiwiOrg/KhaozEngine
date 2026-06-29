using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Shared wire encodings so a <see cref="WorldServer"/> and its <see cref="WorldClient"/> agree.</summary>
public static class MoveProtocol
{
    /// <summary>Type id of <see cref="ReplicatedPosition"/> in the shared registry.</summary>
    public const ushort PositionTypeId = 1;

    /// <summary>Type id of <see cref="MovementState"/> (vertical axis) in the shared registry.</summary>
    public const ushort MovementTypeId = 2;

    /// <summary>Type id of <see cref="PlayerIdentity"/> (display name) in the shared registry.</summary>
    public const ushort IdentityTypeId = 3;

    /// <summary>Upper bound on a replicated <see cref="PlayerIdentity.DisplayName"/>'s UTF-8 encoding, in bytes.
    /// The codec truncates a longer name on write (at a UTF-8 char boundary) and clamps on read, so a hostile or
    /// corrupt name can never exceed this on the wire or blow the read buffer.</summary>
    public const int MaxDisplayNameBytes = 64;

    /// <summary>The replicated-component registry (must match on server and client).</summary>
    public static ReplicationRegistry CreateRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<ReplicatedPosition>(
            PositionTypeId,
            write: (p, bw) => { bw.Write(p.Value.X); bw.Write(p.Value.Y); bw.Write(p.Value.Z); },
            read: br => new ReplicatedPosition { Value = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()) },
            lerp: (a, b, t) => new ReplicatedPosition { Value = Vector3.Lerp(a.Value, b.Value, t) });
        // Vertical movement state. Not interpolated: remotes render from ReplicatedPosition; only the local owner
        // reads this (as its exact authoritative reconciliation basis), and the booleans/timers must not be blended.
        r.Register<MovementState>(
            MovementTypeId,
            write: (m, bw) =>
            {
                bw.Write(m.VerticalVelocity);
                bw.Write(m.Grounded);
                bw.Write(m.TimeSinceGrounded);
                bw.Write(m.JumpBufferRemaining);
            },
            read: br => new MovementState
            {
                VerticalVelocity = br.ReadSingle(),
                Grounded = br.ReadBoolean(),
                TimeSinceGrounded = br.ReadSingle(),
                JumpBufferRemaining = br.ReadSingle(),
            });
        // Display name. Length-prefixed UTF-8, capped at MaxDisplayNameBytes. Not interpolated (strings do not blend);
        // re-sent in every AoI snapshot (names are static, so this is wasteful but simple and consistent at the
        // MaxPlayers scale this server targets). The cap is enforced on BOTH ends: write truncates, read clamps -
        // a player can never push an oversized string onto the wire (cf. the hostile-safe TryDecodeMove path).
        r.Register<PlayerIdentity>(
            IdentityTypeId,
            write: (pi, bw) => WriteDisplayName(bw, pi.DisplayName),
            read: br => new PlayerIdentity { DisplayName = ReadDisplayName(br) });
        return r;
    }

    /// <summary>Writes a display name as <c>[ushort byteLen][UTF-8 bytes]</c>, truncated to
    /// <see cref="MaxDisplayNameBytes"/> at a UTF-8 character boundary so a multibyte glyph is never split.</summary>
    private static void WriteDisplayName(System.IO.BinaryWriter bw, string? name)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(name ?? string.Empty);
        int len = utf8.Length;
        if (len > MaxDisplayNameBytes)
        {
            len = MaxDisplayNameBytes;
            // Back up off a continuation byte (0b10xxxxxx) so we cut on a character boundary, not mid-codepoint.
            while (len > 0 && (utf8[len] & 0xC0) == 0x80) len--;
        }
        bw.Write((ushort)len);
        bw.Write(utf8, 0, len);
    }

    /// <summary>Reads a display name written by <see cref="WriteDisplayName"/>. The declared length is clamped to
    /// <see cref="MaxDisplayNameBytes"/> before allocating, and any surplus is skipped, so a corrupt/oversized
    /// length prefix can neither over-allocate nor desync the rest of the entity's component stream.</summary>
    private static string ReadDisplayName(System.IO.BinaryReader br)
    {
        int declared = br.ReadUInt16();
        int take = Math.Min(declared, MaxDisplayNameBytes);
        byte[] bytes = br.ReadBytes(take);
        for (int surplus = declared - take; surplus > 0; surplus--) br.ReadByte(); // stay frame-aligned (defensive)
        return Encoding.UTF8.GetString(bytes);
    }

    // Move: [seq:int][move.x:float][move.y:float][run:byte][cameraYaw:float][jump:byte] = 18 bytes.
    private const int MoveSize = 4 + 4 + 4 + 1 + 4 + 1;

    /// <summary>Encodes a client move command (including the jump bit).</summary>
    public static byte[] EncodeMove(int seq, in MoveCommand cmd)
    {
        var b = new byte[MoveSize];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), seq);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), cmd.Move.X);
        BitConverter.TryWriteBytes(b.AsSpan(8, 4), cmd.Move.Y);
        b[12] = cmd.Run ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(b.AsSpan(13, 4), cmd.CameraYaw);
        b[17] = cmd.Jump ? (byte)1 : (byte)0;
        return b;
    }

    /// <summary>Decodes a client move command. False (hostile-safe) if the payload is malformed: too short, or
    /// carrying a NaN/infinite move axis or camera yaw.</summary>
    public static bool TryDecodeMove(ReadOnlySpan<byte> data, out int seq, out MoveCommand cmd)
    {
        if (data.Length >= MoveSize)
        {
            float moveX = BitConverter.ToSingle(data.Slice(4, 4));
            float moveY = BitConverter.ToSingle(data.Slice(8, 4));
            float yaw = BitConverter.ToSingle(data.Slice(13, 4));
            // Hostile-safe: a reverse-engineered client can put any bit pattern on the wire, so reject a NaN or
            // infinite move axis / camera yaw as malformed. Left unchecked, a NaN slips past CircleBounds.Clamp
            // (every NaN comparison is false) and replicates a poisoned position to every client in range; an Inf
            // axis normalizes to a NaN direction. Reject here so a poisoned value never reaches the authoritative sim.
            if (!float.IsFinite(moveX) || !float.IsFinite(moveY) || !float.IsFinite(yaw))
            {
                seq = -1;
                cmd = default;
                return false;
            }
            seq = BitConverter.ToInt32(data.Slice(0, 4));
            bool run = data[12] != 0;
            bool jump = data[17] != 0;
            cmd = new MoveCommand(new Vector2(moveX, moveY), run, yaw, jump);
            return true;
        }
        seq = -1;
        cmd = default;
        return false;
    }

    // Server->client frame: [localNetId:int][ackSeq:int][snapshot bytes...].
    private const int FrameHeader = 8;

    /// <summary>Prepends the per-client header (the receiver's own net id + last-acked move seq) to a snapshot.</summary>
    public static byte[] EncodeSnapshotFrame(int localNetId, int ackSeq, byte[] snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var b = new byte[FrameHeader + snapshot.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), localNetId);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), ackSeq);
        snapshot.CopyTo(b.AsSpan(FrameHeader));
        return b;
    }

    /// <summary>Splits a server frame into its header and the replication snapshot. False if too short.</summary>
    public static bool TryDecodeSnapshotFrame(ReadOnlySpan<byte> data, out int localNetId, out int ackSeq, out byte[] snapshot)
    {
        if (data.Length >= FrameHeader)
        {
            localNetId = BitConverter.ToInt32(data.Slice(0, 4));
            ackSeq = BitConverter.ToInt32(data.Slice(4, 4));
            snapshot = data.Slice(FrameHeader).ToArray();
            return true;
        }
        localNetId = -1;
        ackSeq = -1;
        snapshot = Array.Empty<byte>();
        return false;
    }
}
