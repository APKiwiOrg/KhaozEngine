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

    /// <summary>Cap on a replicated route, in steps, and the ONE definition of that number: it is also the ceiling
    /// on <see cref="TileMoveOptions.MaxRouteSteps"/>, which is where the cap is actually enforced. The SIMULATOR
    /// truncates a longer pathfinder result, identically on both heads, so a route that reaches this encoder is
    /// already within the cap and the walk ends at the truncated route's last tile (as far as one click carries).
    /// <para>The encoder therefore REFUSES a longer route rather than truncating one. Truncating on the wire loses
    /// more than the tail: <see cref="TileRoute.End"/> is the DESTINATION, so a route shortened here would tell the
    /// owner it is walking somewhere it was never routed to, and it would keep saying so, with a different wrong
    /// answer, on every snapshot. A stack trace on the head that built an over-long route is the cheaper failure.
    /// </para></summary>
    public const int MaxRouteSteps = 256;

    /// <summary>Cap on a display name's UTF-8 encoding, in bytes. Enforced on write, where the name is truncated at
    /// a codepoint boundary, and on read, where a longer declared length is a malformed frame rather than something
    /// to clamp: no encoder on this wire emits one.</summary>
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
    /// <para>Every reader here is hostile-safe in one of two ways. A field whose whole byte range is meaningful is
    /// CLAMPED, because there is no such thing as a malformed one. A field with a declared length is CHECKED
    /// against the component's OWN framed payload, and a frame that lies about it throws, which
    /// <c>ClientReplicationView.TryApply</c> turns into a false and the caller turns into a disconnect. Reading a
    /// lying frame as a short one would rebuild a route out of bytes that belong to another component, which is a
    /// plausible-looking answer to a question nobody asked. Bounding the check at the payload rather than at the
    /// end of the stream is what makes it fire on a real snapshot: with another entity behind this one, an
    /// unbounded check finds the bytes the lie asked for and passes.</para>
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
        // The step counter is the OTHER half of that division, and TileMoveState documents it as always below the
        // total. Left unclamped, a 250 against a total of 2 rides the wire intact and reads as a step fraction of
        // 125 the moment the owner merges its route back in to build a reconcile basis: a position 125 tiles out,
        // fed straight to the reconcile error and the hard-snap gate. It is harmless only while the route is idle.
        if (s.StepTicks >= s.StepTotal) s.StepTicks = (byte)(s.StepTotal - 1);
        return s;
    }

    // Refuses rather than truncates, for the reason in MaxRouteSteps' doc. A route over the cap cannot come from
    // TileMoveSimulator, which truncates at TileMoveOptions.MaxRouteSteps on both heads, so one here was built by
    // hand and is a local bug: worth the stack, exactly as an over-long game-message payload is.
    static void WriteRoute(TileRouteState v, BinaryWriter w)
    {
        TileDirection[] steps = v.Remaining ?? Array.Empty<TileDirection>();
        if (steps.Length > MaxRouteSteps)
            throw new ArgumentException(
                $"A replicated route is capped at {MaxRouteSteps} steps and this one carries {steps.Length}. " +
                "TileMoveSimulator truncates at TileMoveOptions.MaxRouteSteps, so this route was built elsewhere.",
                nameof(v));
        w.Write((ushort)steps.Length);
        for (int i = 0; i < steps.Length; i++) w.Write((byte)steps[i]);
    }

    // The bytes still inside THIS component's framed payload. ClientReplicationView hands an extension codec a
    // reader bounded to exactly its length-prefixed payload, so that is what this measures, and the distinction is
    // the whole point of measuring it: against the snapshot, a lying declared length is served out of the FOLLOWING
    // components' bytes whenever anything rides behind this component, and the apply returns true carrying a value
    // rebuilt from them. Only a lie that ran off the END of the stream would be caught. A non-seekable stream
    // cannot be measured at all, so it answers "no bound" and falls through to the short-read check at the read.
    static long PayloadRemaining(BinaryReader r)
        => r.BaseStream.CanSeek ? r.BaseStream.Length - r.BaseStream.Position : long.MaxValue;

    // A declared count over the cap, or one this component's own payload cannot satisfy, is a MALFORMED frame: the
    // encoder above refuses to emit either. Throwing is what turns it into a false out of
    // ClientReplicationView.TryApply (and a disconnect) instead of a route silently rebuilt from whatever bytes
    // followed. Both checks run BEFORE the count is allocated against, so a hostile ushort can neither
    // over-allocate nor reach the stack allocation below.
    static TileRouteState ReadRoute(BinaryReader r)
    {
        int declared = r.ReadUInt16();
        if (declared > MaxRouteSteps)
            throw new InvalidDataException($"A replicated route declares {declared} steps, over the {MaxRouteSteps} cap.");
        long available = PayloadRemaining(r);
        if (available < declared)
            throw new InvalidDataException(
                $"A replicated route declares {declared} steps behind {available} payload bytes.");
        Span<byte> raw = stackalloc byte[declared];
        if (r.Read(raw) != declared)   // the unbounded fallback: a non-seekable stream has no length to check first
            throw new InvalidDataException($"A replicated route declares {declared} steps the frame does not hold.");
        var steps = new TileDirection[declared];
        for (int i = 0; i < declared; i++)
            steps[i] = raw[i] <= (byte)TileDirection.NE ? (TileDirection)raw[i] : TileDirection.W;
        return new TileRouteState { Remaining = steps };
    }

    // Truncated at a UTF-8 CODEPOINT boundary, never at the byte cap: cutting inside a multi-byte sequence ships
    // half a codepoint and the receiver decodes U+FFFD, so a name whose 64th byte lands mid-glyph would arrive
    // visibly broken. Backing off the continuation bytes costs at most three bytes of a name already at the cap.
    static void WriteIdentity(TileIdentity v, BinaryWriter w)
    {
        byte[] text = Encoding.UTF8.GetBytes(v.DisplayName ?? string.Empty);
        int take = Math.Min(text.Length, MaxDisplayNameBytes);
        while (take > 0 && take < text.Length && (text[take] & 0xC0) == 0x80) take--;
        w.Write((ushort)take);
        w.Write(text, 0, take);
    }

    // Checked on the same rule as the route, and for the same reason. The bytes themselves are decoded LENIENTLY:
    // Encoding.UTF8.GetString substitutes U+FFFD for an invalid sequence rather than throwing, which is what keeps
    // this reader total. A strict decoder here would hand a remote peer an exception in the apply loop.
    static TileIdentity ReadIdentity(BinaryReader r)
    {
        int declared = r.ReadUInt16();
        if (declared > MaxDisplayNameBytes)
            throw new InvalidDataException(
                $"A replicated display name declares {declared} bytes, over the {MaxDisplayNameBytes} cap.");
        long available = PayloadRemaining(r);
        if (available < declared)
            throw new InvalidDataException(
                $"A replicated display name declares {declared} bytes behind {available} payload bytes.");
        Span<byte> text = stackalloc byte[declared];
        if (r.Read(text) != declared)   // the unbounded fallback, as in ReadRoute
            throw new InvalidDataException($"A replicated display name declares {declared} bytes the frame does not hold.");
        return new TileIdentity { DisplayName = Encoding.UTF8.GetString(text) };
    }
}
