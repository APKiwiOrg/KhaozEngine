using System;
using System.IO;
using System.Text;
using KhaozEngine.Replication;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The replicated half of the tile wire: which components cross it, under which extension ids, and how each one
/// encodes. Both heads build their registry from <see cref="CreateRegistry"/>, so the ids and the codecs cannot
/// drift apart the way two hand-written registrations would.
/// </summary>
public static partial class TileProtocol
{
    /// <summary>Extension id of <see cref="TileMoveState"/>. Registered NEAREST-SAMPLED rather than interpolated,
    /// because a tile is discrete (see <see cref="CreateRegistry"/>).</summary>
    public const ushort TileMoveStateTypeId = ReplicationRegistry.FirstExtensionTypeId + 0;

    /// <summary>Extension id of <see cref="TileRouteState"/>, the owner-only route.</summary>
    public const ushort TileRouteStateTypeId = ReplicationRegistry.FirstExtensionTypeId + 1;

    /// <summary>Extension id of <see cref="TileIdentity"/>.</summary>
    public const ushort TileIdentityTypeId = ReplicationRegistry.FirstExtensionTypeId + 2;

    /// <summary>The first id a GAME may register, with room left below it for this package to add a component
    /// without silently colliding with a game that already shipped. Everything from
    /// <see cref="ReplicationRegistry.FirstExtensionTypeId"/> up to here belongs to the tile netcode.</summary>
    public const ushort FirstGameTypeId = ReplicationRegistry.FirstExtensionTypeId + 8;

    /// <summary>Cap on a replicated route, in steps. A longer route is TRUNCATED on the wire rather than refused,
    /// which costs the owner only the tail of what it can predict ahead: the next snapshot carries the rest, and
    /// by then the walk has moved into it.</summary>
    public const int MaxRouteSteps = 256;

    /// <summary>Cap on a display name's UTF-8 encoding, in bytes. Clamped on write AND on read, because the write
    /// side protects the wire from this head and the read side protects this head from the wire.</summary>
    public const int MaxDisplayNameBytes = 64;

    /// <summary>
    /// Builds the registry BOTH heads must agree on, then hands it to <paramref name="registerExtensions"/> for the
    /// game's own components, which belong at or above <see cref="FirstGameTypeId"/>. A single factory is the point:
    /// a server and a client that build their registries independently agree only for as long as somebody keeps
    /// two lists in step.
    /// <para><see cref="TileMoveState"/> registers with <c>discreteSample: true</c>. It rides the same delayed
    /// render timeline an interpolated component does, but the buffered sample nearest the render time is written
    /// VERBATIM rather than blended, which is the only correct answer for an integer tile. Blending two samples
    /// would put a remote player between two squares it was never standing on, and every rules question asked of
    /// that state (which tile, which region, in reach of what) would be asked of a tile that does not exist. The
    /// smooth motion an observer sees comes from the step fraction the state already carries, not from the
    /// replication layer.</para>
    /// <para>The route is NOT part of <see cref="TileMoveState"/>'s encoding. It rides
    /// <see cref="TileRouteState"/> on the owner-only channel, so an observer's snapshot carries a tile plus step
    /// progress and nothing else. The presentation fields are never written at all, on either component.</para>
    /// </summary>
    public static ReplicationRegistry CreateRegistry(Action<ReplicationRegistry>? registerExtensions = null)
    {
        var reg = new ReplicationRegistry();
        reg.Register<TileMoveState>(TileMoveStateTypeId, WriteMove, ReadMove, discreteSample: true);
        reg.Register<TileRouteState>(TileRouteStateTypeId, WriteRoute, ReadRoute,
            channels: ReplicationChannels.Default | ReplicationChannels.OwnerOnly);
        reg.Register<TileIdentity>(TileIdentityTypeId, WriteIdentity, ReadIdentity);
        registerExtensions?.Invoke(reg);
        return reg;
    }

    // 25 fixed bytes. The plane rides in one byte, matching the command frame, so the two agree about what a plane
    // index can be and a world deeper than 256 planes fails in one place rather than two.
    static void WriteMove(TileMoveState v, BinaryWriter w)
    {
        w.Write(v.Tile.X);
        w.Write(v.Tile.Z);
        w.Write((byte)v.Tile.Plane);
        w.Write((byte)v.Facing);
        w.Write((byte)v.Mode);
        w.Write(v.StepTicks);
        w.Write(v.StepTotal);
        w.Write(v.Epoch);
        w.Write(v.InteractTarget);
    }

    // Every byte here is attacker controlled, and a byte cast into an enum is not validated by the runtime, so the
    // two enums are clamped rather than cast. An unclamped facing would reach TileDirections.Delta, which throws on
    // a direction that does not exist, and an unclamped mode would reach a step-cadence lookup that has no case for
    // it. Both would surface as an exception in a render loop rather than as the corrupt frame they are.
    static TileMoveState ReadMove(BinaryReader r)
    {
        int x = r.ReadInt32(), z = r.ReadInt32();
        int plane = r.ReadByte();
        byte facing = r.ReadByte();
        var s = TileMoveState.At(new TileCoord(x, z, plane),
            facing <= (byte)TileDirection.NE ? (TileDirection)facing : TileDirection.S);
        byte mode = r.ReadByte();
        s.Mode = mode <= (byte)TileMoveMode.Run ? (TileMoveMode)mode : TileMoveMode.Walk;
        s.StepTicks = r.ReadByte();
        s.StepTotal = r.ReadByte();
        s.Epoch = r.ReadUInt32();
        s.InteractTarget = r.ReadInt64();
        if (s.StepTotal == 0) s.StepTotal = 1;   // a hostile 0 would divide by zero in StepFraction
        return s;
    }

    static void WriteRoute(TileRouteState v, BinaryWriter w)
    {
        TileDirection[] steps = v.Remaining ?? Array.Empty<TileDirection>();
        int count = Math.Min(steps.Length, MaxRouteSteps);
        w.Write((ushort)count);
        for (int i = 0; i < count; i++) w.Write((byte)steps[i]);
    }

    // ReadBytes is what makes this total: it returns SHORT at the end of the stream instead of throwing, so a
    // declared count that outruns the frame yields a shorter route rather than an exception out of the apply. The
    // declared count is capped before it is allocated against, so it can neither over-allocate nor be trusted.
    static TileRouteState ReadRoute(BinaryReader r)
    {
        int declared = r.ReadUInt16();
        byte[] raw = r.ReadBytes(Math.Min(declared, MaxRouteSteps));
        var steps = new TileDirection[raw.Length];
        for (int i = 0; i < raw.Length; i++)
            steps[i] = raw[i] <= (byte)TileDirection.NE ? (TileDirection)raw[i] : TileDirection.W;
        Skip(r, declared - raw.Length);
        return new TileRouteState { Remaining = steps };
    }

    static void WriteIdentity(TileIdentity v, BinaryWriter w)
    {
        byte[] text = Encoding.UTF8.GetBytes(v.DisplayName ?? string.Empty);
        int take = Math.Min(text.Length, MaxDisplayNameBytes);
        w.Write((ushort)take);
        w.Write(text, 0, take);
    }

    static TileIdentity ReadIdentity(BinaryReader r)
    {
        int declared = r.ReadUInt16();
        byte[] text = r.ReadBytes(Math.Min(declared, MaxDisplayNameBytes));
        Skip(r, declared - text.Length);
        return new TileIdentity { DisplayName = Encoding.UTF8.GetString(text) };
    }

    // Steps over the bytes a clamp declined to read, so a codec that read less than the frame declared leaves the
    // stream where the next component starts. The extension framing re-aligns after every component anyway, so
    // this is belt and braces for the capture paths that read a payload without that framing around it. The count
    // is bounded by a ushort, so the transient buffer is at most 64 KB and only ever on a malformed frame.
    static void Skip(BinaryReader r, int count)
    {
        if (count > 0) r.ReadBytes(count);
    }
}
