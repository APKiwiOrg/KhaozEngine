using System;
using System.Text;
using KhaozEngine.Netcode;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The rest of the tile wire: the connect token, the server's snapshot frame, the opaque game-message envelope
/// that runs in both directions, and the out-of-band notice.
/// <para>The tag byte is a PER-DIRECTION namespace, which is why
/// <see cref="ClientFrameCommand"/> and <see cref="ServerFrameSnapshot"/> are both 0 and
/// <see cref="ClientFrameGameMessage"/> and <see cref="ServerFrameGameMessage"/> are both 1. A receiver already
/// knows which way a frame arrived, so spending a distinct byte on each direction would buy nothing and would
/// halve the tag space a future frame kind can grow into.</para>
/// <para>Because the two directions share the tag space, one pair genuinely can collide: a client game message
/// (tag 1) and a client command frame (tag 0) travel the SAME way, and the command frame is a fixed 24 bytes
/// while the envelope is not. The tags already separate them, and the PAD RULE separates them a second time on
/// length: whenever an envelope's natural length would land exactly on the command frame's size, one byte is
/// appended so it cannot. Two independent discriminators is the point. A demux that ever keys on length (a
/// transport bridge, a capture replayed out of direction, a log analyzer) then still cannot mistake one for the
/// other, and EVERY other frame family here carries the same rule, so no frame on this wire is an exception to
/// it. That includes the two that travel server-to-client only, the notice and the snapshot, where the direction
/// argument alone would have excused them: a rule with an argued exemption in it is one a later reader has to
/// re-derive, and the snapshot's exemption was already wrong in one place (a snapshot whose body is two bytes
/// lands exactly on a command's 24). The snapshot pays a flags byte per frame for it, the same byte a game
/// message pays, which is what makes the pad unambiguous to strip on the way back in.</para>
/// <para>Every FRAME decoder here (the <c>TryDecode*</c> family) is total, on the same grounds as the command
/// decoder: it returns false for a truncated, mis-tagged, over-capped or internally inconsistent frame and never
/// throws, because the bytes come from a remote peer. The COMPONENT readers in this type's other partial do not
/// share that shape and are not meant to: a payload that lies about a declared length throws
/// <see cref="System.IO.InvalidDataException"/> by design, and the conversion to a false plus a reason happens one
/// layer out, in <c>ClientReplicationView.TryApply</c>'s catch, which the caller treats as terminal for the
/// session. <see cref="CreateRegistry"/> carries the reasoning for refusing a lying length there rather than
/// clamping it.</para>
/// </summary>
public static partial class TileProtocol
{
    /// <summary>Client-to-server tag: an opaque game message. Distinct from <see cref="ClientFrameCommand"/>, which
    /// is what keeps the movement stream and the game's own traffic on one connection without a second channel.</summary>
    public const byte ClientFrameGameMessage = 1;

    /// <summary>Server-to-client tag: a replication snapshot behind its per-client header.</summary>
    public const byte ServerFrameSnapshot = 0;

    /// <summary>Server-to-client tag: an opaque game message, the mirror of <see cref="ClientFrameGameMessage"/>.</summary>
    public const byte ServerFrameGameMessage = 1;

    /// <summary>Server-to-client tag: an out-of-band notice carrying a stable reason token the client localizes.
    /// This is how a refusal the client must act on (a <c>CannotReach</c>, a drain, a kick) arrives without being
    /// smuggled into the snapshot stream, where a client that dropped a snapshot would miss it.</summary>
    public const byte ServerFrameNotice = 2;

    /// <summary>The tag a decoder answers for an EMPTY frame, which is no tag at all. A real one is never 0xFF, so
    /// a demux switch gets a default case rather than an index into a zero-length span.</summary>
    public const byte NoFrameTag = 0xFF;

    /// <summary>Cap on a game-message payload, in bytes. Over it the encoder throws (a local bug, worth the stack)
    /// and the decoder refuses (a remote frame, worth a dropped packet and nothing more).</summary>
    public const int MaxGameMessageBytes = 1024;

    /// <summary>Cap on a notice reason token's UTF-8 encoding, in bytes. A token is a wire symbol the client looks
    /// up, never a sentence, so this is generous rather than tight.</summary>
    public const int MaxNoticeBytes = 128;

    // [tag:1][localNetId:8][ackSeq:4][serverTick:8][flags:1], then the replication snapshot, then the pad byte
    // when the flag says so. The flags byte is the LAST header byte, so it is at SnapshotHeader - 1.
    const int SnapshotHeader = 1 + 8 + 4 + 8 + 1;
    const byte SnapshotFlagPadded = 0x01;

    // [tag:1][kind:2][flags:1], then the opaque payload, then the pad byte when the flag says so.
    const int GameMessageHeader = 1 + 2 + 1;
    const byte GameMessageFlagPadded = 0x01;

    // [tag:1][len:1], then the token's UTF-8 bytes, then the pad byte when the declared length implies one.
    const int NoticeHeader = 1 + 1;

    /// <summary>The leading tag of a client-to-server frame, or <see cref="NoFrameTag"/> for an empty one.</summary>
    public static byte ClientFrameTag(ReadOnlySpan<byte> data) => data.Length == 0 ? NoFrameTag : data[0];

    /// <summary>The leading tag of a server-to-client frame, or <see cref="NoFrameTag"/> for an empty one.</summary>
    public static byte ServerFrameTag(ReadOnlySpan<byte> data) => data.Length == 0 ? NoFrameTag : data[0];

    /// <summary>
    /// The connect token a tile client presents: the protocol-version layer wrapping the world-hash layer wrapping
    /// the game's real auth token, which is exactly the nest <see cref="ConnectionGate.Wrap"/> peels. It goes
    /// through <see cref="ConnectionGate"/> rather than through a tile-specific handshake so a tile server admits
    /// peers under the SAME rules, the same refusal tokens and the same ban path as every other engine server.
    /// </summary>
    public static byte[] BuildConnectToken(string protocolVersion, string worldHash, byte[]? authToken) =>
        ConnectionGate.BuildToken(protocolVersion, worldHash, authToken);

    /// <summary>
    /// Prepends the per-client header to a replication snapshot: the receiver's OWN net id (so a client can pick
    /// itself out of an area-of-interest snapshot that names everyone by net id), the last command sequence the
    /// server consumed (so reconciliation knows which of its pending commands are settled), and the server tick the
    /// snapshot was taken on.
    /// <para>The tick does NOT place the sample on the delayed render timeline, whatever a reader of the field name
    /// might assume: <c>ClientReplicationView</c> stamps that timeline from its own arrival clock and nothing in
    /// <c>Replication</c> reads a server tick at all. It is here for the OWNER's path, where a reconcile has to name
    /// the authoritative tick its basis belongs to, and for a log that has to line two heads up. It is a
    /// <see langword="long"/> because a tick count is one (<c>FixedTickHost.TickCount</c>), while
    /// <c>ClientPrediction.Reconcile</c> takes an <see langword="int"/>, so the client seam owns that narrowing.</para>
    /// <para>Padded off the command frame's size on the same rule as every other frame here, see the type doc.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    public static byte[] EncodeSnapshotFrame(long localNetId, int ackSeq, long serverTick, byte[] snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int natural = SnapshotHeader + snapshot.Length;
        bool pad = natural == CommandFrameSize;
        var b = new byte[natural + (pad ? 1 : 0)];
        b[0] = ServerFrameSnapshot;
        BitConverter.TryWriteBytes(b.AsSpan(1, 8), localNetId);
        BitConverter.TryWriteBytes(b.AsSpan(9, 4), ackSeq);
        BitConverter.TryWriteBytes(b.AsSpan(13, 8), serverTick);
        b[SnapshotHeader - 1] = pad ? SnapshotFlagPadded : (byte)0;
        snapshot.CopyTo(b.AsSpan(SnapshotHeader));
        return b;
    }

    /// <summary>Splits a snapshot frame back into its header and the replication bytes. False (never throws) when
    /// the frame is shorter than the header, carries another tag, is exactly <see cref="EncodeCommand"/>'s fixed
    /// size (which the pad rule means this encoder never emits), or claims a pad byte it has no room for. The
    /// snapshot is COPIED out rather than sliced, because <c>ClientReplicationView.Apply</c> takes an array and the
    /// copy is one allocation per snapshot on a path that already allocated the frame.</summary>
    public static bool TryDecodeSnapshotFrame(ReadOnlySpan<byte> data, out long localNetId, out int ackSeq,
        out long serverTick, out byte[] snapshot)
    {
        localNetId = -1;
        ackSeq = -1;
        serverTick = -1;
        snapshot = Array.Empty<byte>();
        if (data.Length < SnapshotHeader || data.Length == CommandFrameSize || data[0] != ServerFrameSnapshot)
            return false;

        int end = data.Length;
        if ((data[SnapshotHeader - 1] & SnapshotFlagPadded) != 0) end--;
        // An all-header frame claiming a trailing pad byte would slice a negative length and throw out of the
        // receive loop, the same cheapest denial of service the game-message decoder refuses.
        if (end < SnapshotHeader) return false;

        localNetId = BitConverter.ToInt64(data.Slice(1, 8));
        ackSeq = BitConverter.ToInt32(data.Slice(9, 4));
        serverTick = BitConverter.ToInt64(data.Slice(13, 8));
        snapshot = data.Slice(SnapshotHeader, end - SnapshotHeader).ToArray();
        return true;
    }

    /// <summary>
    /// Wraps an opaque game payload in its direction tag and its game-defined kind. The engine never looks inside
    /// the payload: what a kind means is the game's business, and keeping it opaque is what stops this package
    /// growing a message vocabulary it would then have to version.
    /// <para>When the natural frame length would land exactly on the command frame's fixed size, one zero pad byte
    /// is appended and the padded flag set, so an envelope can never share a length with a command. See the type
    /// doc for why that matters when the tags already differ.</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="frameTag"/> is not an envelope tag, or
    /// <paramref name="payload"/> is longer than <see cref="MaxGameMessageBytes"/>.</exception>
    public static byte[] EncodeGameMessage(byte frameTag, ushort kind, ReadOnlySpan<byte> payload)
    {
        // A tag this method does not own produces a frame the matching decoder refuses, so the message is dropped
        // silently on a wire that is otherwise loud about local bugs. Passing ClientFrameCommand is the one that
        // matters: it builds something that looks like a command frame and is not.
        if (frameTag != ClientFrameGameMessage && frameTag != ServerFrameGameMessage)
            throw new ArgumentException(
                $"A game message rides tag {ClientFrameGameMessage} in either direction, not {frameTag}.", nameof(frameTag));
        if (payload.Length > MaxGameMessageBytes)
            throw new ArgumentException($"A game message payload is capped at {MaxGameMessageBytes} bytes.", nameof(payload));
        int natural = GameMessageHeader + payload.Length;
        bool pad = natural == CommandFrameSize;
        var b = new byte[natural + (pad ? 1 : 0)];
        b[0] = frameTag;
        BitConverter.TryWriteBytes(b.AsSpan(1, 2), kind);
        b[3] = pad ? GameMessageFlagPadded : (byte)0;
        payload.CopyTo(b.AsSpan(GameMessageHeader));
        return b;
    }

    /// <summary>
    /// Splits a game message. False (never throws) when it is shorter than the header, carries another tag, is
    /// exactly <see cref="EncodeCommand"/>'s fixed size (always a command, never an envelope), claims a pad byte it
    /// has no room for, or carries more payload than <see cref="MaxGameMessageBytes"/>. The returned payload is a
    /// SLICE of <paramref name="data"/> with any pad byte already removed, so nothing is copied and nothing is
    /// interpreted.
    /// </summary>
    public static bool TryDecodeGameMessage(ReadOnlySpan<byte> data, byte frameTag, out ushort kind,
        out ReadOnlySpan<byte> payload)
    {
        kind = 0;
        payload = default;
        if (data.Length < GameMessageHeader || data.Length == CommandFrameSize || data[0] != frameTag) return false;

        int end = data.Length;
        if ((data[3] & GameMessageFlagPadded) != 0) end--;
        // A hostile frame that is all header and claims a pad byte would slice a negative length and throw out of
        // the receive loop, which is the cheapest denial of service on this wire. Refuse it instead.
        if (end < GameMessageHeader) return false;
        if (end - GameMessageHeader > MaxGameMessageBytes) return false;

        kind = BitConverter.ToUInt16(data.Slice(1, 2));
        payload = data.Slice(GameMessageHeader, end - GameMessageHeader);
        return true;
    }

    /// <summary>Encodes an out-of-band notice as a length-prefixed reason token, padded off the command frame's size
    /// on the same rule as a game message.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="reasonToken"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="reasonToken"/> encodes to more than
    /// <see cref="MaxNoticeBytes"/> UTF-8 bytes.</exception>
    public static byte[] EncodeNotice(string reasonToken)
    {
        ArgumentNullException.ThrowIfNull(reasonToken);
        byte[] text = Encoding.UTF8.GetBytes(reasonToken);
        if (text.Length > MaxNoticeBytes)
            throw new ArgumentException($"A notice token is capped at {MaxNoticeBytes} bytes.", nameof(reasonToken));
        int natural = NoticeHeader + text.Length;
        bool pad = natural == CommandFrameSize;
        var b = new byte[natural + (pad ? 1 : 0)];
        b[0] = ServerFrameNotice;
        b[1] = (byte)text.Length;
        text.CopyTo(b.AsSpan(NoticeHeader));
        return b;
    }

    /// <summary>
    /// Reads a notice. The declared length must account for the WHOLE frame, pad byte included, so a frame carrying
    /// a length that disagrees with the bytes actually present is refused rather than read as a prefix. That is
    /// stricter than it needs to be for a datagram and deliberately so: a lying length is the shape a probe takes,
    /// and there is no legitimate sender that produces one. Nothing is allocated before the length is checked, so a
    /// corrupt frame can neither over-allocate nor throw.
    /// </summary>
    public static bool TryDecodeNotice(ReadOnlySpan<byte> data, out string reasonToken)
    {
        reasonToken = string.Empty;
        if (data.Length < NoticeHeader || data[0] != ServerFrameNotice) return false;
        int declared = data[1];
        if (declared > MaxNoticeBytes) return false;
        int natural = NoticeHeader + declared;
        int expected = natural == CommandFrameSize ? natural + 1 : natural;
        if (data.Length != expected) return false;
        reasonToken = Encoding.UTF8.GetString(data.Slice(NoticeHeader, declared));
        return true;
    }
}
