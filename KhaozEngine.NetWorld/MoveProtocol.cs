using System;
using System.Numerics;
using System.Text;
using KhaozEngine.Locomotion;
using KhaozEngine.Replication;

namespace KhaozEngine.NetWorld;

/// <summary>Shared wire encodings so a <see cref="WorldServer"/> and its <see cref="WorldClient"/> agree.</summary>
public static class MoveProtocol
{
    /// <summary>
    /// The engine wire-format generation. Bumped only on a breaking change to the on-the-wire snapshot / delta /
    /// frame-header layout, so it labels the incompatible generations. It is <c>4</c> as of the teleport-epoch feature,
    /// which added the authoritative teleport epoch (<see cref="MovementState.TeleportEpoch"/>, 4 bytes) to the
    /// movement built-in's codec (id <see cref="MovementTypeId"/>). <c>3</c> was the swim feature, which added the
    /// surface-swim flag (<see cref="MovementState.Swimming"/>) to the same codec. Both are built-in components (NOT
    /// length-prefixed, so an older client cannot skip the extra bytes), hence breaking wire changes. <c>2</c> was the 10.0.0 line, which widened
    /// <see cref="NetId"/> from 32-bit to 64-bit on the wire (the snapshot/delta netId field and the
    /// <see cref="EncodeSnapshotFrame"/> header). <c>1</c> was the pre-10.0.0 32-bit line. Engine built-ins and the
    /// codec ids are otherwise unchanged.
    ///
    /// Since 10.2.0 the engine enforces this generation AUTOMATICALLY at connect: every <see cref="WorldClient"/>
    /// folds it into its Hello (see <see cref="ProtocolHandshake.BuildClientToken"/>) even with no consumer
    /// <see cref="WorldClientConfig.ProtocolVersion"/>, and <see cref="WorldServer"/> / <see cref="ShardedWorldServer"/>
    /// always install a <see cref="WireGenerationAuthenticator"/> that rejects a mismatch (or a peer presenting none)
    /// cleanly as <see cref="DisconnectReason.IncompatibleVersion"/>, so a 10.0.0 peer and a pre-10.0.0 peer reject
    /// each other at connect instead of misparsing a 64-bit frame as 32-bit, with no consumer action required (the old
    /// advice to fold <c>;wire{N}</c> into the consumer version string is obsolete). The consumer
    /// <see cref="WorldClientConfig.ProtocolVersion"/> game-version gate still layers on top via
    /// <see cref="VersionCheckingAuthenticator"/>.
    /// </summary>
    public const int WireProtocolVersion = 4;

    /// <summary>Type id of <see cref="ReplicatedPosition"/> in the shared registry.</summary>
    public const ushort PositionTypeId = 1;

    /// <summary>Type id of <see cref="MovementState"/> (vertical axis) in the shared registry.</summary>
    public const ushort MovementTypeId = 2;

    /// <summary>Type id of <see cref="PlayerIdentity"/> (display name) in the shared registry.</summary>
    public const ushort IdentityTypeId = 3;

    /// <summary>Type id of <see cref="DynamicBodyState"/> (a replicated dynamic rigid body's orientation + velocity)
    /// in the shared registry. The body's position rides on <see cref="ReplicatedPosition"/> (id
    /// <see cref="PositionTypeId"/>) alongside it, so it drives area-of-interest and interpolates like any other
    /// entity; this component adds the interpolated orientation.</summary>
    public const ushort DynamicBodyTypeId = 4;

    /// <summary>The lowest type id a consumer may register on top of the movement protocol (an NPC kind, HP,
    /// faction, …). Ids <c>1..15</c> are reserved for engine movement built-ins (currently
    /// <see cref="PositionTypeId"/>/<see cref="MovementTypeId"/>/<see cref="IdentityTypeId"/>/<see cref="DynamicBodyTypeId"/>);
    /// consumer components
    /// registered at or above this floor are length-prefixed on the wire, so a client that never registered the id
    /// SKIPS it instead of disconnecting (see <see cref="ReplicationRegistry.FirstExtensionTypeId"/>). Register the
    /// SAME extra components on both the server and its clients via
    /// <see cref="CreateRegistry(System.Action{ReplicationRegistry})"/>.</summary>
    public const ushort FirstConsumerTypeId = ReplicationRegistry.FirstExtensionTypeId;

    /// <summary>Upper bound on a replicated <see cref="PlayerIdentity.DisplayName"/>'s UTF-8 encoding, in bytes.
    /// The codec truncates a longer name on write (at a UTF-8 char boundary) and clamps on read, so a hostile or
    /// corrupt name can never exceed this on the wire or blow the read buffer.</summary>
    public const int MaxDisplayNameBytes = 64;

    /// <summary>Builds the replicated-component registry (the movement built-ins), optionally letting a consumer
    /// register its own extra components on top via <paramref name="configure"/>. It must produce the SAME registry
    /// on the server and every client, so call it identically on both ends (pass it to the
    /// <c>WorldServer</c>/<c>ShardedWorldServer</c>/<c>WorldClient</c> registry ctor param). Register consumer
    /// components at ids >= <see cref="FirstConsumerTypeId"/>; those are length-prefixed on the wire so a client that
    /// predates a given component simply skips it (no disconnect), while an unknown built-in id still hard-fails.</summary>
    public static ReplicationRegistry CreateRegistry(Action<ReplicationRegistry>? configure = null)
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
                bw.Write(m.Swimming);       // wire generation 3: the surface-swim flag rides alongside the vertical axis
                bw.Write(m.TeleportEpoch);  // wire generation 4: the authoritative teleport epoch (hard-cut marker)
            },
            read: br => new MovementState
            {
                VerticalVelocity = br.ReadSingle(),
                Grounded = br.ReadBoolean(),
                TimeSinceGrounded = br.ReadSingle(),
                JumpBufferRemaining = br.ReadSingle(),
                Swimming = br.ReadBoolean(),
                TeleportEpoch = br.ReadUInt32(),
            });
        // Display name. Length-prefixed UTF-8, capped at MaxDisplayNameBytes. Not interpolated (strings do not blend);
        // re-sent in every AoI snapshot (names are static, so this is wasteful but simple and consistent at the
        // MaxPlayers scale this server targets). The cap is enforced on BOTH ends: write truncates, read clamps -
        // a player can never push an oversized string onto the wire (cf. the hostile-safe TryDecodeMove path).
        r.Register<PlayerIdentity>(
            IdentityTypeId,
            write: (pi, bw) => WriteDisplayName(bw, pi.DisplayName),
            read: br => new PlayerIdentity { DisplayName = ReadDisplayName(br) });
        // Dynamic rigid body: the orientation quaternion (4 floats) + linear/angular velocity (3 floats each) that
        // ride alongside the body's ReplicatedPosition. Interpolatable: the orientation SLERPs between snapshots on
        // the client's fixed-delay buffer (the same machinery that glides a remote player's position), so a spinning
        // crate rotates smoothly between the ~tick-rate snapshots. Velocity is NOT blended (it is a rate, carried for
        // extrapolation/effects), so the lerp snaps it to the target sample - it is only ever read off the newest
        // applied snapshot in practice.
        r.Register<DynamicBodyState>(
            DynamicBodyTypeId,
            write: (d, bw) =>
            {
                bw.Write(d.Orientation.X); bw.Write(d.Orientation.Y); bw.Write(d.Orientation.Z); bw.Write(d.Orientation.W);
                bw.Write(d.LinearVelocity.X); bw.Write(d.LinearVelocity.Y); bw.Write(d.LinearVelocity.Z);
                bw.Write(d.AngularVelocity.X); bw.Write(d.AngularVelocity.Y); bw.Write(d.AngularVelocity.Z);
            },
            read: br => new DynamicBodyState
            {
                Orientation = new Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                LinearVelocity = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
                AngularVelocity = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle()),
            },
            // Slerp the orientation (normalized: guards against a denormalized blend of two near-equal quaternions);
            // velocity is a rate, so hold the target sample's value rather than blending it.
            lerp: (a, b, t) => new DynamicBodyState
            {
                Orientation = Quaternion.Normalize(Quaternion.Slerp(a.Orientation, b.Orientation, t)),
                LinearVelocity = b.LinearVelocity,
                AngularVelocity = b.AngularVelocity,
            });
        configure?.Invoke(r);   // consumer extension components (ids >= FirstConsumerTypeId)
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

    /// <summary>A client-to-server control message that is NOT a movement command (e.g. a self-rescue / "unstuck"
    /// request). They ride the same Data channel as <see cref="EncodeMove"/>, demuxed by <b>length</b>: a control
    /// frame is <see cref="ClientControlSize"/> bytes, a move is <see cref="MoveSize"/> (18) - they never alias. The
    /// payload is deliberately small and carries no position: the server alone owns the response (the destination of a
    /// self-rescue), so a reverse-engineered client cannot turn it into a teleport-anywhere.</summary>
    public enum ClientControlKind : byte
    {
        /// <summary>Ask the authoritative server to move THIS player to a server-decided safe position
        /// (return-to-spawn / unstuck). The server resolves the destination and rate-limits it.</summary>
        SelfRescue = 1,

        /// <summary>Advertise that this client understands delta replication (<see cref="ServerFrameKind.Delta"/>).
        /// Sent once on join. A delta-aware server then serves this client per-tick AoI deltas instead of full
        /// snapshots; a server that predates the feature decodes it as a control with an unknown kind and ignores it
        /// (its switch only acts on <see cref="SelfRescue"/>), so the hello is harmless across version skew and the
        /// client keeps receiving full snapshots.</summary>
        DeltaCapable = 2,
    }

    // Control frame: [marker:byte][kind:byte] = 2 bytes. The marker is a fixed sentinel so a random 2-byte packet is
    // unlikely to be taken for a control message; the size alone already keeps it distinct from an 18-byte move.
    private const byte ClientControlMarker = 0xC5;
    private const int ClientControlSize = 2;

    /// <summary>Encodes a client control message as <c>[marker][kind]</c>. Shorter than a move frame, so a server
    /// that predates this feature decodes it as a (too-short) malformed move and harmlessly ignores it - the request
    /// becomes a no-op across version skew rather than a protocol break.</summary>
    public static byte[] EncodeClientControl(ClientControlKind kind) => new byte[] { ClientControlMarker, (byte)kind };

    /// <summary>Decodes a client control message written by <see cref="EncodeClientControl"/>. False for anything that
    /// is not exactly a 2-byte marker-prefixed control frame - in particular a full move payload, so the server's
    /// receive path can try this first and fall through to <see cref="TryDecodeMove"/> for ordinary input.</summary>
    public static bool TryDecodeClientControl(ReadOnlySpan<byte> data, out ClientControlKind kind)
    {
        if (data.Length == ClientControlSize && data[0] == ClientControlMarker)
        {
            kind = (ClientControlKind)data[1];
            return true;
        }
        kind = default;
        return false;
    }

    // Replication ack frame: [ClientControlMarker:0xC5][ReplicationAckMarker:0xA0][appliedSeq:int] = 6 bytes. Shares
    // the control marker family but is demuxed by its distinct length (6), so it never aliases a 2-byte control or an
    // 18-byte move. A client only ever sends this AFTER it has received a Delta frame, so a server that predates the
    // feature never sees one; the receive path still tries this before the control/move decodes.
    private const byte ReplicationAckMarker = 0xA0;
    private const int ReplicationAckSize = 6;

    /// <summary>Encodes the client-to-server replication ack carrying the snapshot seq the client last applied
    /// (<see cref="ClientReplicationView.LastAppliedSeq"/>). The server feeds it to
    /// <see cref="AoiDeltaReplicator.Acknowledge"/> to advance that client's delta baseline.</summary>
    public static byte[] EncodeReplicationAck(int appliedSeq)
    {
        var b = new byte[ReplicationAckSize];
        b[0] = ClientControlMarker;
        b[1] = ReplicationAckMarker;
        BitConverter.TryWriteBytes(b.AsSpan(2, 4), appliedSeq);
        return b;
    }

    /// <summary>Decodes a replication ack written by <see cref="EncodeReplicationAck"/>. False for anything that is not
    /// exactly a 6-byte marker-prefixed ack (a move or a control decodes false here), so the server's receive path can
    /// try this alongside the control/move decodes without them aliasing.</summary>
    public static bool TryDecodeReplicationAck(ReadOnlySpan<byte> data, out int appliedSeq)
    {
        if (data.Length == ReplicationAckSize && data[0] == ClientControlMarker && data[1] == ReplicationAckMarker)
        {
            appliedSeq = BitConverter.ToInt32(data.Slice(2, 4));
            return true;
        }
        appliedSeq = -1;
        return false;
    }

    // Game message (client->server): [ClientControlMarker:0xC5][GameMessageMarker:0xB0][kind:ushort][flags:byte][payload...].
    // A game-defined message (attack, interaction, chat, inventory op, …) demuxed from the move stream. It rides the
    // same 0xC5 control-marker family as the control/ack frames but carries its OWN sub-marker (0xB0, distinct from the
    // 0xA0 replication ack), so a game message is never mistaken for an ack ([1]==0xA0) and vice-versa. The header is 5
    // bytes; the payload is opaque engine-side.
    //
    // Aliasing-with-the-move contract (the demux keys the move on LENGTH 18): a move is ALWAYS exactly 18 bytes, so the
    // game-message decode REJECTS length 18 outright (a length-18 frame is always a move, never a game message) and the
    // encoder guarantees it NEVER emits an 18-byte frame - when the natural length would be 18 it appends one pad byte
    // (→ 19) and sets flags bit0 so the decoder strips it. Demux ORDER on the server receive path is therefore:
    //   1. replication ack   (length 6, [1]==0xA0)
    //   2. client control    (length 2)
    //   3. game message      (length >= 5 and != 18, [0]==0xC5 && [1]==0xB0)   <-- tried BEFORE the move
    //   4. move              (length >= 18)
    // Because game-message is tried before move yet never itself lands on 18, a real move (exactly 18) always falls
    // through to the move decode, and a real game message (never 18) is always claimed here first - the two can never
    // alias. See WorldServer.HandleData / ShardedWorldServer.HandleData.
    private const byte GameMessageMarker = 0xB0;
    private const int GameMessageHeader = 5;   // marker + sub-marker + ushort kind + flags byte
    private const byte GameMessageFlagPadded = 0x01;

    /// <summary>Encodes a client-to-server game message as <c>[0xC5][0xB0][kind:ushort][flags][payload]</c>. When the
    /// natural frame length would be exactly 18 (the move length the server demuxes on), one pad byte is appended and
    /// the padded flag set, so a game message can NEVER alias an 18-byte move. Decode with
    /// <see cref="TryDecodeGameMessage"/>.</summary>
    public static byte[] EncodeGameMessage(ushort kind, ReadOnlySpan<byte> payload)
    {
        int natural = GameMessageHeader + payload.Length;
        bool pad = natural == MoveSize;   // never emit an 18-byte frame (it would be read as a move)
        var b = new byte[natural + (pad ? 1 : 0)];
        b[0] = ClientControlMarker;
        b[1] = GameMessageMarker;
        BitConverter.TryWriteBytes(b.AsSpan(2, 2), kind);
        b[4] = pad ? GameMessageFlagPadded : (byte)0;
        payload.CopyTo(b.AsSpan(GameMessageHeader));
        // (the trailing pad byte, if any, stays 0)
        return b;
    }

    /// <summary>Decodes a client-to-server game message written by <see cref="EncodeGameMessage"/>. False (never throws)
    /// for anything that is not a well-formed game-message frame: too short (&lt; 5), an exactly-18-byte frame (always a
    /// move, never a game message - see the aliasing contract above), one lacking the <c>[0xC5][0xB0]</c> marker pair, or
    /// a pad-flagged frame too short to hold any payload once the pad byte is stripped (a hostile 5-byte pad-flagged
    /// frame - rejected here rather than underflowing the payload slice). The returned
    /// <paramref name="payload"/> is a slice of <paramref name="data"/> (no copy) with any trailing pad byte removed;
    /// it is opaque to the engine. The server's receive path tries this AFTER the ack/control decodes and BEFORE the
    /// move decode.</summary>
    public static bool TryDecodeGameMessage(ReadOnlySpan<byte> data, out ushort kind, out ReadOnlySpan<byte> payload)
    {
        // Reject an 18-byte frame first: it is always a move (the encoder never emits an 18-byte game message), so a
        // legitimate move whose first two bytes happen to be 0xC5/0xB0 is never stolen here.
        if (data.Length >= GameMessageHeader && data.Length != MoveSize
            && data[0] == ClientControlMarker && data[1] == GameMessageMarker)
        {
            int end = data.Length;
            if ((data[4] & GameMessageFlagPadded) != 0) end--;   // strip the single disambiguation pad byte
            // Hostile-safe: a pad-flagged frame with no room for a payload after the strip (end < header) would slice a
            // NEGATIVE length and THROW out of the server's Poll loop, killing every session (a 5-byte pad-flagged frame
            // is the minimal DoS). Reject it as malformed instead - it falls through to the move decode and is flagged.
            if (end < GameMessageHeader) { kind = 0; payload = default; return false; }
            kind = BitConverter.ToUInt16(data.Slice(2, 2));
            payload = data.Slice(GameMessageHeader, end - GameMessageHeader);
            return true;
        }
        kind = 0;
        payload = default;
        return false;
    }

    // Game message (server->client): the [kind:ushort][payload...] body carried inside a ServerFrameKind.GameMessage
    // envelope. No aliasing concern in this direction - the 1-byte ServerFrameKind tag already discriminates it from a
    // snapshot/notice/delta, and an older client's frame demux ignores an unknown kind (see TryDecodeServerFrame).
    /// <summary>Encodes the body of a server-to-client game message: <c>[kind:ushort][payload]</c>. Wrap it with
    /// <c>EncodeServerFrame(ServerFrameKind.GameMessage, body)</c> to put it on the Data channel.</summary>
    public static byte[] EncodeGameMessageBody(ushort kind, ReadOnlySpan<byte> payload)
    {
        var b = new byte[2 + payload.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0, 2), kind);
        payload.CopyTo(b.AsSpan(2));
        return b;
    }

    /// <summary>Splits a server-to-client game-message body (the payload of a <see cref="ServerFrameKind.GameMessage"/>
    /// frame) into its kind and opaque payload. False if too short to hold the 2-byte kind. The
    /// <paramref name="payload"/> is a slice of <paramref name="data"/> (no copy).</summary>
    public static bool TryDecodeGameMessageBody(ReadOnlySpan<byte> data, out ushort kind, out ReadOnlySpan<byte> payload)
    {
        if (data.Length >= 2)
        {
            kind = BitConverter.ToUInt16(data.Slice(0, 2));
            payload = data.Slice(2);
            return true;
        }
        kind = 0;
        payload = default;
        return false;
    }

    // Server->client frame: [localNetId:long(8)][ackSeq:int(4)][snapshot bytes...]. localNetId widened to 64-bit in
    // 10.0.0 (was int), so the header grew 8 -> 12 bytes. This is a wire break gated on the ProtocolVersion handshake.
    private const int FrameHeader = 12;

    /// <summary>Prepends the per-client header (the receiver's own net id + last-acked move seq) to a snapshot.</summary>
    public static byte[] EncodeSnapshotFrame(long localNetId, int ackSeq, byte[] snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        var b = new byte[FrameHeader + snapshot.Length];
        BitConverter.TryWriteBytes(b.AsSpan(0, 8), localNetId);
        BitConverter.TryWriteBytes(b.AsSpan(8, 4), ackSeq);
        snapshot.CopyTo(b.AsSpan(FrameHeader));
        return b;
    }

    /// <summary>Splits a server frame into its header and the replication snapshot. False if too short.</summary>
    public static bool TryDecodeSnapshotFrame(ReadOnlySpan<byte> data, out long localNetId, out int ackSeq, out byte[] snapshot)
    {
        if (data.Length >= FrameHeader)
        {
            localNetId = BitConverter.ToInt64(data.Slice(0, 8));
            ackSeq = BitConverter.ToInt32(data.Slice(8, 4));
            snapshot = data.Slice(FrameHeader).ToArray();
            return true;
        }
        localNetId = -1;
        ackSeq = -1;
        snapshot = Array.Empty<byte>();
        return false;
    }

    /// <summary>Upper bound on a <see cref="ServerNotice.Message"/>'s UTF-8 encoding, in bytes. Truncated on write
    /// (at a char boundary) and clamped on read, so a corrupt length can neither over-allocate nor desync.</summary>
    public const int MaxNoticeMessageBytes = 256;

    /// <summary>Upper bound on a <see cref="ServerNotice.Payload"/>, in bytes (same hostile-safe contract).</summary>
    public const int MaxNoticePayloadBytes = 512;

    // Notice: [kind:byte][flags:byte][secondsUntil:float?][msgLen:ushort][msg utf8][payloadLen:ushort][payload].
    // flags bit0 = secondsUntil present. Lengths are capped on write and clamped on read.
    private const byte NoticeFlagHasSeconds = 0x01;

    /// <summary>Encodes a <see cref="ServerNotice"/>. Message + payload are capped at their byte limits.</summary>
    public static byte[] EncodeNotice(in ServerNotice notice)
    {
        using var ms = new System.IO.MemoryStream();
        using var bw = new System.IO.BinaryWriter(ms);
        bw.Write((byte)notice.Kind);
        byte flags = notice.SecondsUntil.HasValue ? NoticeFlagHasSeconds : (byte)0;
        bw.Write(flags);
        if (notice.SecondsUntil.HasValue) bw.Write(notice.SecondsUntil.Value);
        WriteCapped(bw, Encoding.UTF8.GetBytes(notice.Message ?? string.Empty), MaxNoticeMessageBytes, utf8Boundary: true);
        WriteCapped(bw, notice.Payload ?? Array.Empty<byte>(), MaxNoticePayloadBytes, utf8Boundary: false);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Best-effort decode of a notice frame. Never throws: a short/corrupt buffer yields a safe default
    /// (Custom, empty message, no seconds, empty payload), and declared lengths are clamped before allocating.</summary>
    public static ServerNotice DecodeNotice(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2) return new ServerNotice(ServerNoticeKind.Custom, string.Empty);
        int i = 0;
        var kind = (ServerNoticeKind)data[i++];
        byte flags = data[i++];
        float? seconds = null;
        if ((flags & NoticeFlagHasSeconds) != 0)
        {
            if (data.Length < i + 4) return new ServerNotice(kind, string.Empty);
            seconds = BitConverter.ToSingle(data.Slice(i, 4));
            i += 4;
            if (!float.IsFinite(seconds.Value)) seconds = null;   // hostile-safe: drop a NaN/Inf countdown
        }
        string message = ReadCapped(data, ref i, MaxNoticeMessageBytes, out byte[] msgBytes)
            ? Encoding.UTF8.GetString(msgBytes) : string.Empty;
        byte[] payload = ReadCapped(data, ref i, MaxNoticePayloadBytes, out byte[] payloadBytes) ? payloadBytes : Array.Empty<byte>();
        return new ServerNotice(kind, message, seconds, payload);
    }

    private static void WriteCapped(System.IO.BinaryWriter bw, byte[] bytes, int cap, bool utf8Boundary)
    {
        int len = Math.Min(bytes.Length, cap);
        if (utf8Boundary)
            while (len > 0 && len < bytes.Length && (bytes[len] & 0xC0) == 0x80) len--;  // do not split a codepoint
        bw.Write((ushort)len);
        bw.Write(bytes, 0, len);
    }

    private static bool ReadCapped(ReadOnlySpan<byte> data, ref int i, int cap, out byte[] bytes)
    {
        if (data.Length < i + 2) { bytes = Array.Empty<byte>(); return false; }
        int declared = BitConverter.ToUInt16(data.Slice(i, 2));
        i += 2;
        int take = Math.Min(Math.Min(declared, cap), Math.Max(0, data.Length - i));
        bytes = data.Slice(i, take).ToArray();
        i += declared;   // advance by the declared length so a later field stays frame-aligned (clamped read above)
        return true;
    }

    /// <summary>The kind of server-to-client frame riding the Data channel: a per-client full snapshot, an out-of-band
    /// notice, or a per-client area-of-interest <b>delta</b>. The first byte of every server-to-client Data payload. A
    /// <see cref="Delta"/> frame carries the same <c>[localNetId][ackSeq]</c> header (via <see cref="EncodeSnapshotFrame"/>)
    /// as a <see cref="Snapshot"/>, but its body is an <see cref="AoiDeltaReplicator"/> delta the client applies with
    /// <see cref="ClientReplicationView.ApplyDelta"/>; it is only sent to a client that advertised
    /// <see cref="ClientControlKind.DeltaCapable"/>, so an older client only ever receives <see cref="Snapshot"/>. A
    /// <see cref="GameMessage"/> frame carries a game-defined <c>[kind:ushort][payload]</c> body (see
    /// <see cref="EncodeGameMessageBody"/>) an older client's demux simply ignores as an unknown kind (its
    /// <c>OnServerFrame</c> switch has no case for it), so it is version-skew-safe downstream.</summary>
    public enum ServerFrameKind : byte { Snapshot = 0, Notice = 1, Delta = 2, GameMessage = 3 }

    /// <summary>Wraps a server-to-client payload with its 1-byte kind tag so snapshots and
    /// notices share the Data channel. The receiver demuxes via <see cref="TryDecodeServerFrame"/>.</summary>
    public static byte[] EncodeServerFrame(ServerFrameKind kind, ReadOnlySpan<byte> payload)
    {
        var b = new byte[1 + payload.Length];
        b[0] = (byte)kind;
        payload.CopyTo(b.AsSpan(1));
        return b;
    }

    /// <summary>Splits a server frame into its kind and inner payload. False if empty.</summary>
    public static bool TryDecodeServerFrame(ReadOnlySpan<byte> data, out ServerFrameKind kind, out byte[] payload)
    {
        if (data.Length >= 1)
        {
            kind = (ServerFrameKind)data[0];
            payload = data.Slice(1).ToArray();
            return true;
        }
        kind = ServerFrameKind.Snapshot;
        payload = Array.Empty<byte>();
        return false;
    }
}
