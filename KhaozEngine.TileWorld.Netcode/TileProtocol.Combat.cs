using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld.Netcode;

/// <summary>
/// The combat frame family: every swing one tick resolved, for the viewers whose interest holds the target.
/// <para>EXPLICIT rather than derived, and the temptation to derive it is worth closing off. The serve is a full
/// snapshot every tick, so a client CAN diff health between two samples, and the diff is wrong twice: two hits on one
/// tick collapse into one number, and a MISS moves health by zero and is therefore invisible. A fight rendered from
/// health deltas shows fewer, larger, later hitsplats than the fight the server ran.</para>
/// <para><c>[tag:1][count:1]</c> then <c>count</c> x
/// <c>[attacker:8][target:8][amount:2][kind:1][flags:1]</c>, twenty bytes an event. <c>flags</c> bit 0 is
/// <see cref="TileCombatEvent.Landed"/> and bit 1 is <see cref="TileCombatEvent.Killed"/>, so a death rides the blow
/// that caused it and a client never has to notice an entity's absence to know it died.</para>
/// <para>The PAD RULE applies here as it does to every other family on this wire: when the natural length would land
/// exactly on the command frame's fixed size, one byte is appended so a demux that ever keys on LENGTH still cannot
/// mistake the two. No FLAG byte says so, because unlike a snapshot or a game message this frame's length is fully
/// determined by its own count byte, so the decoder recomputes the expected size and strips the difference. That is
/// the notice frame's shape, and it is the right one for the same reason.</para>
/// </summary>
public static partial class TileProtocol
{
    /// <summary>The most events one frame carries. The count rides in ONE byte, so this is the wire's own ceiling
    /// rather than a policy. A tick that somehow produced more is a local bug worth a stack, exactly as an over-long
    /// game-message payload is.</summary>
    public const int MaxCombatEvents = 255;

    const int CombatHeader = 1 + 1;
    const int CombatEventSize = 8 + 8 + 2 + 1 + 1;
    const byte CombatFlagLanded = 0x01;
    const byte CombatFlagKilled = 0x02;

    /// <summary>Encodes one tick's swings.</summary>
    /// <param name="events">The swings, at most <see cref="MaxCombatEvents"/> of them.</param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is null.</exception>
    /// <exception cref="ArgumentException">There are more than <see cref="MaxCombatEvents"/>.</exception>
    public static byte[] EncodeCombat(IReadOnlyList<TileCombatEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        if (events.Count > MaxCombatEvents)
            throw new ArgumentException($"A combat frame is capped at {MaxCombatEvents} events.", nameof(events));
        int natural = CombatHeader + events.Count * CombatEventSize;
        bool pad = natural == CommandFrameSize;
        var b = new byte[natural + (pad ? 1 : 0)];
        b[0] = ServerFrameCombat;
        b[1] = (byte)events.Count;
        for (int i = 0; i < events.Count; i++)
        {
            TileCombatEvent e = events[i];
            int at = CombatHeader + i * CombatEventSize;
            BitConverter.TryWriteBytes(b.AsSpan(at, 8), e.AttackerNetId);
            BitConverter.TryWriteBytes(b.AsSpan(at + 8, 8), e.TargetNetId);
            BitConverter.TryWriteBytes(b.AsSpan(at + 16, 2), e.Amount);
            b[at + 18] = e.Kind;
            b[at + 19] = (byte)((e.Landed ? CombatFlagLanded : 0) | (e.Killed ? CombatFlagKilled : 0));
        }
        return b;
    }

    /// <summary>
    /// Reads a combat frame into <paramref name="into"/>, which is CLEARED first so a caller can reuse one list
    /// forever. False (never throws) for a frame that is shorter than the header, carries another tag, or whose
    /// length disagrees with its own count in either direction. That last check is what makes the reader total
    /// without a single bounds test in the loop: once the length matches the count exactly, every slice below is
    /// inside the frame by construction.
    /// </summary>
    /// <param name="data">The frame.</param>
    /// <param name="into">The list to fill. Null is refused rather than allocated for.</param>
    public static bool TryDecodeCombat(ReadOnlySpan<byte> data, List<TileCombatEvent> into)
    {
        if (into is null) return false;
        into.Clear();
        if (data.Length < CombatHeader || data[0] != ServerFrameCombat) return false;

        int count = data[1];
        int natural = CombatHeader + count * CombatEventSize;
        int expected = natural == CommandFrameSize ? natural + 1 : natural;
        if (data.Length != expected) return false;

        for (int i = 0; i < count; i++)
        {
            int at = CombatHeader + i * CombatEventSize;
            byte flags = data[at + 19];
            into.Add(new TileCombatEvent(
                BitConverter.ToInt64(data.Slice(at, 8)),
                BitConverter.ToInt64(data.Slice(at + 8, 8)),
                BitConverter.ToUInt16(data.Slice(at + 16, 2)),
                data[at + 18],
                (flags & CombatFlagLanded) != 0,
                (flags & CombatFlagKilled) != 0));
        }
        return true;
    }
}
