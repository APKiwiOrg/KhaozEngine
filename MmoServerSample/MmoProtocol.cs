using System;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;

namespace MmoServerSample;

/// <summary>A 2D world position - the one replicated/migrated gameplay component in this reference server.</summary>
public struct Position : IComponent
{
    public float X;
    public float Y;
}

/// <summary>A client's per-tick movement input: a position delta to apply to its player.</summary>
public readonly record struct MoveCommand(float Dx, float Dy);

/// <summary>Shared wire helpers so the server and its clients agree on encodings.</summary>
public static class MmoProtocol
{
    /// <summary>Replicated-component registry shared by server and client (must match on both ends).</summary>
    public static ReplicationRegistry CreateRegistry()
    {
        var r = new ReplicationRegistry();
        r.Register<Position>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Position { X = br.ReadSingle(), Y = br.ReadSingle() },
            lerp: (a, b, t) => new Position { X = a.X + (b.X - a.X) * t, Y = a.Y + (b.Y - a.Y) * t });
        return r;
    }

    /// <summary>Reads an entity's <see cref="Position"/> for the shard host's border/handoff/AoI math.</summary>
    public static bool PositionAccessor(World world, Entity entity, out float x, out float y)
    {
        if (world.TryGet(entity, out Position p)) { x = p.X; y = p.Y; return true; }
        x = y = 0f;
        return false;
    }

    /// <summary>Encodes a client move command: <c>[seq:int][dx:float][dy:float]</c>.</summary>
    public static byte[] EncodeMove(int seq, MoveCommand command)
    {
        var bytes = new byte[12];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), seq);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), command.Dx);
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 4), command.Dy);
        return bytes;
    }

    /// <summary>Decodes a client move command. False if the payload is malformed (hostile-safe).</summary>
    public static bool TryDecodeMove(ReadOnlySpan<byte> data, out int seq, out MoveCommand command)
    {
        if (data.Length >= 12)
        {
            seq = BitConverter.ToInt32(data.Slice(0, 4));
            command = new MoveCommand(BitConverter.ToSingle(data.Slice(4, 4)), BitConverter.ToSingle(data.Slice(8, 4)));
            return true;
        }
        seq = -1;
        command = default;
        return false;
    }
}
