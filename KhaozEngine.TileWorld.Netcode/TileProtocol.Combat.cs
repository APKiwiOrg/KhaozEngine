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
        return EncodeCombat(events, 0, events.Count);
    }

    /// <summary>
    /// Encodes ONE FRAME'S WORTH of a longer run of swings, which is what lets a caller holding more than
    /// <see cref="MaxCombatEvents"/> of them chunk instead of failing. The count rides in one byte, so the ceiling
    /// is the wire's own and no encoder can lift it: what a caller can do is send several frames, and a decoder
    /// needs no help with that because it reads each frame on its own terms and a head raises the events in the
    /// order the frames arrive.
    /// <para>This is the overload the SERVE uses, and the reason it exists is that the whole-list overload above
    /// throws. That throw is correct for a game building a frame by hand (an over-long list is a local bug worth a
    /// stack) and it was wrong inside the serve loop, where it took the tick down for every player on the server
    /// rather than costing the one viewer whose interest happened to hold that many fights.</para>
    /// </summary>
    /// <param name="events">The swings to slice a frame out of.</param>
    /// <param name="start">Index of the first swing to encode.</param>
    /// <param name="count">How many to encode, at most <see cref="MaxCombatEvents"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="start"/> or <paramref name="count"/> is
    /// negative, the pair runs past the end of <paramref name="events"/>, or <paramref name="count"/> is above
    /// <see cref="MaxCombatEvents"/>.</exception>
    public static byte[] EncodeCombat(IReadOnlyList<TileCombatEvent> events, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxCombatEvents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, events.Count - start);
        int natural = CombatHeader + count * CombatEventSize;
        bool pad = natural == CommandFrameSize;
        var b = new byte[natural + (pad ? 1 : 0)];
        b[0] = ServerFrameCombat;
        b[1] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            TileCombatEvent e = events[start + i];
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
