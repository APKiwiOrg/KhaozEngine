using System;
using System.Buffers.Binary;

namespace KhaozEngine.Skills;

/// <summary>The durable and wire form of a <see cref="SkillBook"/>, for an opaque per-character blob.</summary>
/// <remarks>
/// The layout is a byte version, a byte count, then <c>count</c> fixed-width entries of one id byte and an
/// eight-byte little-endian double. Little-endian on every host by construction rather than by luck: every
/// read and every write here goes through <see cref="BinaryPrimitives"/>, so a blob written on one machine
/// reads the same on another.
/// <para>A COUNT change needs no version bump, which is the property that lets a game add a skill without
/// a migration: an entry is fixed width and keyed by id, an older blob's missing skills decode as zero, and
/// a newer blob's higher ids are skipped forward-compatibly by a build that does not have them.</para>
/// </remarks>
public static class SkillBookCodec
{
    /// <summary>The format byte every blob starts with.</summary>
    /// <remarks>Version 2 is what this package reads and writes. Version 1 belonged to a game's own
    /// pre-extraction roster and is not read here: see <see cref="TryDecode"/> for what an unknown version
    /// does and what a game with older bytes is expected to do about them.</remarks>
    public const byte Version = 2;

    /// <summary>The version byte and the count byte.</summary>
    public const int HeaderBytes = 2;

    /// <summary>One entry: the id byte and the eight-byte experience.</summary>
    public const int EntryBytes = 9;

    /// <summary>The most skills a blob can carry, because the count and every id are a single byte.</summary>
    public const int MaxCount = 255;

    /// <summary>The first version byte a skill blob may never use.</summary>
    /// <remarks>A composite record format that wraps a skill blob typically marks itself with a high byte
    /// and tells the two apart by this very byte, so the band 0xF0 to 0xFF is reserved fleet-wide and a
    /// version is never assigned into it. A collision would read every bare skill blob as a malformed
    /// composite and quarantine it.</remarks>
    public const byte ReservedVersionFloor = 0xF0;

    /// <summary>Encodes a book. Never null and never empty, so a stored blob and "no state" stay
    /// distinguishable by a store that treats an empty array as null.</summary>
    /// <param name="book">The book being written.</param>
    /// <exception cref="ArgumentNullException"><paramref name="book"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The book holds more than
    /// <see cref="MaxCount"/> skills, which is refused here rather than truncated into a blob a reader
    /// would decode as a different character.</exception>
    public static byte[] Encode(SkillBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        int count = book.Count;
        if (count > MaxCount)
            throw new ArgumentOutOfRangeException(nameof(book), count,
                $"a skill blob carries at most {MaxCount} skills, because the count and every id are one byte");
        var bytes = new byte[HeaderBytes + (count * EntryBytes)];
        bytes[0] = Version;
        bytes[1] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            int at = HeaderBytes + (i * EntryBytes);
            bytes[at] = (byte)i;
            BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(at + 1), book.Xp(i));
        }
        return bytes;
    }

    /// <summary>The version byte a blob declares, or -1 for null, empty or a blob shorter than its own
    /// header.</summary>
    /// <remarks>The one affordance for a game carrying bytes older than this format. There is deliberately
    /// no legacy reader hook in the package: a pre-extraction version belongs to ONE game's old roster,
    /// where ids changed meaning, and only that game can say what its id 2 used to be. So it dispatches on
    /// this, migrates its own bytes into a version 2 blob, and hands the result here.</remarks>
    /// <param name="blob">The stored bytes.</param>
    public static int VersionOf(ReadOnlySpan<byte> blob) => blob.Length < HeaderBytes ? -1 : blob[0];

    /// <summary>Decodes a blob. Null, empty or malformed answers false with no book, which is the caller's
    /// cue to seat a fresh one rather than to throw. An UNKNOWN version is malformed by this rule: it is
    /// refused by number rather than guessed at, and <see cref="VersionOf"/> is how a game recognises its
    /// own older bytes before calling here.</summary>
    /// <param name="blob">The stored bytes.</param>
    /// <param name="roster">The roster in force, which sizes the decoded book. A blob declaring a
    /// different count is still read: ids this roster does not have are skipped and ids it has that the
    /// blob omits stay at zero.</param>
    /// <param name="curve">The curve in force, which the decoded book carries and reads its levels off.
    /// The blob stores experience rather than levels, so the curve is the caller's to supply, and a record
    /// whose own curve differs is rescaled with <see cref="SkillXpRescale.Rebase"/> before anybody asks
    /// this book for a level.</param>
    /// <param name="book">The decoded book, or null on a refusal.</param>
    /// <param name="requiredIndex">A skill every well-formed blob must carry an entry for, or -1 for no
    /// such rule. See <see cref="Validate"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="roster"/> or <paramref name="curve"/> is
    /// null.</exception>
    public static bool TryDecode(byte[]? blob, ISkillRoster roster, SkillXpCurve curve, out SkillBook book,
        int requiredIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(curve);
        book = null!;
        if (blob is not { Length: > 0 } || Validate(blob, requiredIndex) is not null) return false;

        var decoded = new SkillBook(roster, curve);
        int count = blob[1];
        for (int i = 0; i < count; i++)
        {
            int at = HeaderBytes + (i * EntryBytes);
            int skill = blob[at];
            // A skill this build does not have is SKIPPED rather than refused: the entry is fixed width, so
            // reading past it is exact, and a character who levelled a skill on a newer build should not be
            // quarantined by an older one.
            if (skill < decoded.Count)
                decoded.SetXp(skill, BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(at + 1)));
        }
        book = decoded;
        return true;
    }

    /// <summary>Vets a blob for a persistence layer. A non-null return is the quarantine reason and is
    /// meant to reject the WHOLE record rather than one field, which is the right severity: a book that
    /// cannot be read is a character whose levels would silently reset.</summary>
    /// <remarks>Roster-free on purpose, so a store can vet bytes without knowing which game wrote them. It
    /// checks SHAPE, and the one content rule it takes is <paramref name="requiredIndex"/>.</remarks>
    /// <param name="blob">The stored bytes. Null or empty is "no state" rather than a fault.</param>
    /// <param name="requiredIndex">A skill every well-formed blob must carry an entry for, or -1 for no
    /// such rule. A decoder cannot tell a missing entry from a stored zero, so a game whose character
    /// sheet is meaningless without one skill (a vitality pool that sets a health bar) names it here and a
    /// blob that lost it is quarantined instead of decoding into a character on default health.</param>
    public static string? Validate(byte[]? blob, int requiredIndex = -1)
    {
        // A caller bug rather than bad data, and it fails LOUD rather than as a permanent quarantine: an id
        // is one byte, so a required index above that can never be found and every blob would be refused.
        if (requiredIndex > MaxCount)
            throw new ArgumentOutOfRangeException(nameof(requiredIndex), requiredIndex,
                $"a skill id is one byte, so a required skill is at most {MaxCount}");
        if (blob is null or { Length: 0 }) return null;   // no state is not a fault
        if (blob.Length < HeaderBytes) return "skill blob is shorter than its own header";
        if (blob[0] != Version) return $"skill blob version {blob[0]}, this build reads {Version}";
        int count = blob[1];
        int expected = HeaderBytes + (count * EntryBytes);
        if (blob.Length != expected)
            return $"skill blob declares {count} skills so it should be {expected} bytes, it is {blob.Length}";
        bool carriesRequired = requiredIndex < 0;
        // One flag per possible id byte, so a duplicate entry is caught without allocating and without
        // assuming the ids arrive in any particular order. 256 bytes of stack, once per record load.
        Span<bool> seen = stackalloc bool[256];
        for (int i = 0; i < count; i++)
        {
            int at = HeaderBytes + (i * EntryBytes);
            // SHAPE rather than presence, and the decoder is why: it takes the LAST entry for a skill, so a
            // blob carrying one twice reads as well-formed while holding two answers to the same question
            // and keeping the one nothing about the record makes visible. Encode never writes one. It
            // refuses a repeated UNKNOWN id as well, which the decoder skips whole and which that argument
            // therefore does not cover: over-strict on purpose, because a blob naming any id twice is
            // malformed whether or not this build reads that id. The entry index leads the message, since
            // that is the coordinate a person diagnosing a quarantine line counts in.
            if (seen[blob[at]]) return $"skill blob entry {i} carries skill {blob[at]} twice";
            seen[blob[at]] = true;
            double xp = BinaryPrimitives.ReadDoubleLittleEndian(blob.AsSpan(at + 1));
            // Two refusals rather than one, because the REASON is the thing a person reads out of a
            // quarantine log line: a finite negative reported as non-finite sends them hunting a NaN that
            // is not there.
            if (!double.IsFinite(xp)) return $"skill blob entry {i} carries a non-finite experience";
            if (xp < 0d) return $"skill blob entry {i} carries {xp} experience, below zero";
            // The ceiling. Without it a corrupted 1e300 passes here, SetXp clamps it on the way in, and the
            // character silently comes back at the top of the table instead of being quarantined.
            if (xp > SkillXpLimits.MaxXp)
                return $"skill blob entry {i} carries {xp} experience, over the {SkillXpLimits.MaxXp} ceiling";
            if (blob[at] == requiredIndex) carriesRequired = true;
        }
        if (!carriesRequired)
            return $"skill blob carries no entry for skill {requiredIndex}, and every book has one";
        return null;
    }
}
