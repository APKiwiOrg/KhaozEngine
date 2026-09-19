using System;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace KhaozEngine.Skills;

/// <summary>The durable form of a <see cref="SkillXpCurve"/>'s parameters: twelve bytes, three
/// little-endian int32 in the order first level cost, doubling levels, max level.</summary>
/// <remarks>
/// It ships here rather than being written again in each game's record codec, because the format is already
/// decided and two implementations of a decided format is one more place for an endianness or an order to
/// differ. It is a SECTION rather than a record: a game writes these twelve bytes beside its own, and what
/// an ABSENT section means is the other half of the contract, which is <see cref="SkillXpCurve.Osrs"/>. A
/// record with no curve section was written before the game had a configurable curve, so the classic table
/// is what its numbers meant, and that is why <see cref="SkillXpCurve.OsrsHash"/> is a durable word rather
/// than a digest of parameters.
/// </remarks>
public static class SkillXpCurveCodec
{
    /// <summary>The section's fixed width: three int32.</summary>
    public const int Bytes = 12;

    /// <summary>The highest level cap a stored section may name.</summary>
    /// <remarks>A decoder's guard rather than a design limit. A curve builds its thresholds eagerly, one
    /// double per level, so four corrupted bytes naming a two-billion level cap would ask for sixteen
    /// gigabytes before anything else got a say. A real cap is in the hundreds, so a section over this is
    /// corruption and reads as unreadable.</remarks>
    public const int MaxLevelCeiling = 10_000;

    /// <summary>Writes a curve's three parameters.</summary>
    /// <param name="curve">The curve being stored. It must be parametric: the classic curve has no
    /// parameters to write, and the way to store it is to write no section at all.</param>
    /// <exception cref="ArgumentNullException"><paramref name="curve"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="curve"/> is not parametric.</exception>
    public static byte[] Encode(SkillXpCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (!curve.IsParametric)
            throw new ArgumentException(
                "the classic curve has no parameters, and an absent section is how it is stored", nameof(curve));
        var bytes = new byte[Bytes];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, curve.FirstLevelCost);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), curve.DoublingLevels);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), curve.MaxLevel);
        return bytes;
    }

    /// <summary>Reads a curve section. False means UNREADABLE, which a caller answers by leaving the stored
    /// experience exactly as it is rather than by guessing a curve: guessing would move every level in the
    /// book.</summary>
    /// <remarks>REBUILT rather than trusted. The three numbers came off disk, and
    /// <see cref="SkillXpCurve.Configured"/> is the same gate a content load passes, so a section holding
    /// zeroes or a negative reads as unreadable instead of building a curve whose thresholds are all zero
    /// and whose every level is the cap.</remarks>
    /// <param name="section">The twelve bytes. Any other length is unreadable.</param>
    /// <param name="curve">The rebuilt curve, or null on a refusal.</param>
    public static bool TryDecode(ReadOnlySpan<byte> section, [NotNullWhen(true)] out SkillXpCurve? curve)
    {
        curve = null;
        if (section.Length != Bytes) return false;
        int firstLevelCost = BinaryPrimitives.ReadInt32LittleEndian(section);
        int doublingLevels = BinaryPrimitives.ReadInt32LittleEndian(section[4..]);
        int maxLevel = BinaryPrimitives.ReadInt32LittleEndian(section[8..]);
        if (maxLevel > MaxLevelCeiling) return false;
        try
        {
            curve = SkillXpCurve.Configured(firstLevelCost, doublingLevels, maxLevel);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        return true;
    }
}
