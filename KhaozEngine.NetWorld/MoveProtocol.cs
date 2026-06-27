using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Shared wire encodings so a <see cref="WorldServer"/> and its <see cref="WorldClient"/> agree.</summary>
public static class MoveProtocol
{
    /// <summary>Type id of <see cref="ReplicatedPosition"/> in the shared registry.</summary>
    public const ushort PositionTypeId = 1;

    /// <summary>The replicated-component registry (must match on server and client).</summary>
    public static ReplicationRegistry CreateRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<ReplicatedPosition>(
            PositionTypeId,
            write: (p, bw) => { bw.Write(p.Value.X); bw.Write(p.Value.Y); bw.Write(p.Value.Z); },
            read: br => new ReplicatedPosition { Value = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()) },
            lerp: (a, b, t) => new ReplicatedPosition { Value = Vector3.Lerp(a.Value, b.Value, t) });
        return r;
    }

    // Move: [seq:int][move.x:float][move.y:float][run:byte][cameraYaw:float] = 17 bytes.
    private const int MoveSize = 4 + 4 + 4 + 1 + 4;

    /// <summary>Encodes a client move command.</summary>
    public static byte[] EncodeMove(int seq, in MoveCommand cmd)
    {
        var b = new byte[MoveSize];
        BitConverter.TryWriteBytes(b.AsSpan(0, 4), seq);
        BitConverter.TryWriteBytes(b.AsSpan(4, 4), cmd.Move.X);
        BitConverter.TryWriteBytes(b.AsSpan(8, 4), cmd.Move.Y);
        b[12] = cmd.Run ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(b.AsSpan(13, 4), cmd.CameraYaw);
        return b;
    }

    /// <summary>Decodes a client move command. False (hostile-safe) if the payload is malformed.</summary>
    public static bool TryDecodeMove(ReadOnlySpan<byte> data, out int seq, out MoveCommand cmd)
    {
        if (data.Length >= MoveSize)
        {
            seq = BitConverter.ToInt32(data.Slice(0, 4));
            var move = new Vector2(BitConverter.ToSingle(data.Slice(4, 4)), BitConverter.ToSingle(data.Slice(8, 4)));
            bool run = data[12] != 0;
            float yaw = BitConverter.ToSingle(data.Slice(13, 4));
            cmd = new MoveCommand(move, run, yaw);
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
