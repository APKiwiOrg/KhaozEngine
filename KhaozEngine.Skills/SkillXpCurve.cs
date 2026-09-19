using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KhaozEngine.Skills;

/// <summary>
/// One experience curve: its thresholds, the two questions anyone asks it, and the identity a saved
/// character compares itself against.
/// </summary>
/// <remarks>
/// A curve is CONTENT rather than a fixed table, so it MOVES: a game tunes the first level's cost, the
/// doubling span and the level cap whenever it likes. A saved character's experience is a number under a
/// curve rather than a level, so the same number reads as a different level the moment the curve moves,
/// which is why this is an OBJECT with an identity rather than a pair of free functions. A record stores
/// the curve it was written under and <see cref="SkillXpRescale"/> carries the character across to the
/// current one.
/// <para>Two shapes exist. <see cref="Configured"/> is the parametric family, where the cost of a level
/// doubles every <c>doublingLevels</c> levels from a first level costing <c>firstLevelCost</c>.
/// <see cref="Osrs"/> is the classic running-sum table, which is not of that family at all and so carries
/// no parameters. That is the whole reason <see cref="Hash"/> exists rather than the three numbers being
/// compared directly: a curve with no parameters still has to be nameable.</para>
/// </remarks>
public sealed class SkillXpCurve
{
    /// <summary>The identity of the classic curve. A word rather than a digest, because it names a shape
    /// rather than a set of parameters, and because a record that carries no curve section at all is read
    /// as this one.</summary>
    public const string OsrsHash = "osrs";

    /// <summary>The level cap the classic curve runs to.</summary>
    public const int OsrsMaxLevel = 99;

    static readonly SkillXpCurve LegacyCurve =
        new(0, 0, OsrsMaxLevel, BuildOsrsThresholds(), OsrsHash);

    // Index IS the level, so [0] is unused and [1] is level one at zero experience. Built once per curve,
    // because the recurrence is not something to recompute per query.
    readonly double[] _thresholds;

    SkillXpCurve(int firstLevelCost, int doublingLevels, int maxLevel, double[] thresholds, string hash)
    {
        FirstLevelCost = firstLevelCost;
        DoublingLevels = doublingLevels;
        MaxLevel = maxLevel;
        Hash = hash;
        _thresholds = thresholds;
    }

    /// <summary>The classic running sum of <c>floor(n + 300 * 2^(n/7))</c>, quartered and floored, to level
    /// <see cref="OsrsMaxLevel"/>.</summary>
    /// <remarks>Kept as a named curve rather than as one more set of parameters, because it is the only
    /// thing that can say what a number stored before a game had a configurable curve MEANT. A record with
    /// no curve section was written under it, which is what makes <see cref="OsrsHash"/> a durable meaning
    /// rather than a convenience.</remarks>
    public static SkillXpCurve Osrs => LegacyCurve;

    /// <summary>The parametric curve, out of the three numbers a game's own tuning names.</summary>
    /// <param name="firstLevelCost">Experience level two costs.</param>
    /// <param name="doublingLevels">Levels the per-level cost takes to double.</param>
    /// <param name="maxLevel">The highest level the curve runs to.</param>
    public static SkillXpCurve Configured(int firstLevelCost, int doublingLevels, int maxLevel)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstLevelCost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(doublingLevels);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLevel, 2);
        return new SkillXpCurve(firstLevelCost, doublingLevels, maxLevel,
            BuildThresholds(firstLevelCost, doublingLevels, maxLevel),
            Digest(firstLevelCost, doublingLevels, maxLevel));
    }

    /// <summary>Experience level two costs. Zero for <see cref="Osrs"/>, which has no such
    /// parameter.</summary>
    public int FirstLevelCost { get; }

    /// <summary>Levels the per-level cost takes to double. Zero for <see cref="Osrs"/>.</summary>
    public int DoublingLevels { get; }

    /// <summary>The highest level this curve reaches.</summary>
    public int MaxLevel { get; }

    /// <summary>This curve's identity, which is what a saved record carries and what a load compares. A
    /// short hex digest of the three parameters, or <see cref="OsrsHash"/> for the classic table.</summary>
    public string Hash { get; }

    /// <summary>Whether this curve has the parameters a record section can store, which is everything
    /// except <see cref="Osrs"/>.</summary>
    public bool IsParametric => !string.Equals(Hash, OsrsHash, StringComparison.Ordinal);

    /// <summary>Experience needed to BE a level. Clamped to the table at both ends.</summary>
    /// <param name="level">The level asked about.</param>
    public double XpForLevel(int level) => _thresholds[Math.Clamp(level, 1, MaxLevel)];

    /// <summary>The level an amount of experience buys, clamped to the table at both ends.</summary>
    /// <param name="xp">The experience held.</param>
    public int LevelFor(double xp)
    {
        if (double.IsNaN(xp) || xp <= 0d) return 1;
        // Linear from the top: a player is usually near their own level, so a binary search buys nothing
        // measurable and costs the reader a boundary condition to check.
        for (int level = MaxLevel; level > 1; level--)
            if (xp >= _thresholds[level]) return level;
        return 1;
    }

    // The cost of a level doubles every doublingLevels levels, and a threshold is the running sum of the
    // costs under it. Rounded per level rather than at the end, so every threshold is a whole number and a
    // game's own numbers land exactly (114 at level 2, 86,276,710 at level 100 on 114 doubling every six).
    static double[] BuildThresholds(int firstLevelCost, int doublingLevels, int maxLevel)
    {
        var thresholds = new double[maxLevel + 1];
        double running = 0d;
        for (int level = 1; level < maxLevel; level++)
        {
            running += Math.Round(firstLevelCost * Math.Pow(2d, (level - 1) / (double)doublingLevels),
                MidpointRounding.AwayFromZero);
            thresholds[level + 1] = running;
        }
        return thresholds;
    }

    // The classic recurrence. Level one is zero by definition and is not produced by the loop.
    static double[] BuildOsrsThresholds()
    {
        var thresholds = new double[OsrsMaxLevel + 1];
        double points = 0d;
        for (int level = 1; level < OsrsMaxLevel; level++)
        {
            points += Math.Floor(level + (300d * Math.Pow(2d, level / 7d)));
            thresholds[level + 1] = Math.Floor(points / 4d);
        }
        return thresholds;
    }

    // The three numbers in a fixed order, digested. Short for the same reason a content hash is: it goes in
    // a log line and in a twelve-byte record section's company, and a collision here would need two curves
    // whose parameters differ and whose first eight bytes of SHA-256 agree.
    static string Digest(int firstLevelCost, int doublingLevels, int maxLevel)
    {
        string text = string.Create(CultureInfo.InvariantCulture,
            $"xp1|{firstLevelCost}|{doublingLevels}|{maxLevel}");
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}
