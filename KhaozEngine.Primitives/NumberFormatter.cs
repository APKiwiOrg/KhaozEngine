using System;
using System.Globalization;

namespace KhaozEngine.Primitives;

/// <summary>Controls how large numbers are rendered by <see cref="NumberFormatter"/>.</summary>
public enum NumberNotation
{
    /// <summary>Short suffixes: 1.23K, 45.6M, 7.89B, 1.23T, ... up to 1e33 (Dc), then scientific.</summary>
    Simple,

    /// <summary>Scientific notation: 1.23E+003, 4.56E+012, ... (the BCL <c>E</c> format).</summary>
    Scientific,

    /// <summary>Engineering notation: the exponent is always a multiple of 3 (1.23E6, 456E9, ...).</summary>
    Engineering,
}

/// <summary>
/// Game-agnostic large-number formatting for display values (currencies, damage, HP, costs, counts). Idle and
/// incremental games in particular need to show values that span many orders of magnitude compactly; this is the
/// one place that logic lives, so every screen reads identically. Three notation modes (<see cref="NumberNotation"/>):
/// <see cref="NumberNotation.Simple"/> uses short suffixes up to 1e33 (Decillion) then falls back to scientific,
/// <see cref="NumberNotation.Scientific"/> and <see cref="NumberNotation.Engineering"/> use exponents throughout.
/// <para>
/// A settable <see cref="Notation"/> is the process-wide default a game binds to its "number format" setting once,
/// so the parameterless <see cref="Format(double, int, int)"/> overloads pick it up everywhere; pass a
/// <see cref="NumberNotation"/> explicitly to override per call. Output is culture-invariant (a decimal point, not
/// a locale separator) so it is deterministic and safe to compose into other strings.
/// </para>
/// Pure and allocation-light (BCL only); no dependency, so it sits in <c>KhaozEngine.Primitives</c> and is usable
/// from a renderer, a headless server, or a balance-simulation tool alike. Non-localizable value tokens (digits +
/// notation suffixes) sit below the localization layer: format the number here, then compose it into a localized
/// string.
/// </summary>
public static class NumberFormatter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Process-wide default notation used by the overloads that do not take one. Bind a game's setting to it once.</summary>
    public static NumberNotation Notation { get; set; } = NumberNotation.Simple;

    // Suffix table: index 0 = 1e3, index 1 = 1e6, ..., index 10 = 1e33.
    static readonly string[] Suffixes =
    [
        "K",   // 1e3   Thousand
        "M",   // 1e6   Million
        "B",   // 1e9   Billion
        "T",   // 1e12  Trillion
        "Qa",  // 1e15  Quadrillion
        "Qi",  // 1e18  Quintillion
        "Sx",  // 1e21  Sextillion
        "Sp",  // 1e24  Septillion
        "Oc",  // 1e27  Octillion
        "No",  // 1e30  Nonillion
        "Dc",  // 1e33  Decillion
    ];

    /// <summary>
    /// Formats <paramref name="value"/> using the current <see cref="Notation"/> default.
    /// </summary>
    /// <param name="value">The value to format. NaN renders as "0", infinity as "Inf".</param>
    /// <param name="decimalsSmall">Decimal places for magnitudes below 1,000 (default 1).</param>
    /// <param name="decimalsLarge">Decimal places for suffixed / scientific magnitudes (default 2).</param>
    public static string Format(double value, int decimalsSmall = 1, int decimalsLarge = 2)
        => Format(value, Notation, decimalsSmall, decimalsLarge);

    /// <summary>
    /// Formats <paramref name="value"/> in the given <paramref name="notation"/> (ignores the <see cref="Notation"/> default).
    /// </summary>
    /// <param name="value">The value to format. NaN renders as "0", infinity as "Inf".</param>
    /// <param name="notation">The notation mode to use for this call.</param>
    /// <param name="decimalsSmall">Decimal places for magnitudes below 1,000 (default 1).</param>
    /// <param name="decimalsLarge">Decimal places for suffixed / scientific magnitudes (default 2).</param>
    public static string Format(double value, NumberNotation notation, int decimalsSmall = 1, int decimalsLarge = 2)
    {
        if (double.IsNaN(value)) return "0";
        if (double.IsInfinity(value)) return "Inf";

        bool negative = value < 0;
        double abs = Math.Abs(value);
        string prefix = negative ? "-" : "";

        return notation switch
        {
            NumberNotation.Scientific => prefix + FormatScientific(abs, decimalsSmall, decimalsLarge),
            NumberNotation.Engineering => prefix + FormatEngineering(abs, decimalsSmall, decimalsLarge),
            _ => prefix + FormatSimple(abs, decimalsSmall, decimalsLarge),
        };
    }

    /// <summary>
    /// Formats <paramref name="value"/> with zero decimals below 1,000 (for integer-like counts: quantities,
    /// depths, levels), using the current <see cref="Notation"/> default.
    /// </summary>
    public static string FormatInt(double value) => Format(value, Notation, decimalsSmall: 0, decimalsLarge: 2);

    /// <summary>As <see cref="FormatInt(double)"/> but in the given <paramref name="notation"/>.</summary>
    public static string FormatInt(double value, NumberNotation notation)
        => Format(value, notation, decimalsSmall: 0, decimalsLarge: 2);

    static string FormatSimple(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (abs < 1000)
            return FormatBelowThousand(abs, decimalsSmall, decimalsLarge);

        string largeFmt = "F" + decimalsLarge.ToString(Inv);

        double threshold = 1000.0;
        for (int i = 0; i < Suffixes.Length; i++)
        {
            double nextThreshold = threshold * 1000.0;
            if (abs < nextThreshold)
                return (abs / threshold).ToString(largeFmt, Inv) + Suffixes[i];
            threshold = nextThreshold;
        }

        // Beyond the suffix table: fall back to scientific.
        return abs.ToString("E" + decimalsLarge.ToString(Inv), Inv);
    }

    static string FormatScientific(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (abs < 1000)
            return FormatBelowThousand(abs, decimalsSmall, decimalsLarge);

        return abs.ToString("E" + decimalsLarge.ToString(Inv), Inv);
    }

    static string FormatEngineering(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (abs < 1000)
            return FormatBelowThousand(abs, decimalsSmall, decimalsLarge);

        // Engineering notation: exponent is always a multiple of 3.
        int exp = (int)Math.Floor(Math.Log10(abs));
        int engExp = exp - exp % 3;
        double mantissa = abs / Math.Pow(10, engExp);

        return mantissa.ToString("F" + decimalsLarge.ToString(Inv), Inv) + "E" + engExp.ToString(Inv);
    }

    // A "F"-formatted decimal count beyond this is pointless: doubles only carry ~15-17 significant decimal
    // digits, so this just bounds pathological output for values approaching double.Epsilon.
    const int MaxSmallValueDecimals = 17;

    /// <summary>
    /// Formats a magnitude below 1,000 (the shared tail of all three notations). Values below 1 get enough
    /// decimal places to show their leading significant digit truthfully - the fixed-decimalsSmall behaviour
    /// (1 decimal by default) silently rounds a value like 0.05 up to "0.1", doubling what it visually reports.
    /// Values at or above 1 - and any call that explicitly asks for zero small-value decimals, e.g.
    /// <see cref="FormatInt(double)"/>'s integer-count contract - are unaffected: exactly <paramref name="decimalsSmall"/>
    /// decimals, unchanged from the original behaviour.
    /// </summary>
    static string FormatBelowThousand(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (decimalsSmall > 0 && abs > 0 && abs < 1)
            return abs.ToString("F" + SmallValueDecimals(abs, decimalsSmall, decimalsLarge).ToString(Inv), Inv);

        return abs.ToString("F" + decimalsSmall.ToString(Inv), Inv);
    }

    /// <summary>
    /// Decimal places needed to show at least one truthful significant digit of a sub-1 magnitude, floored at
    /// <c>max(decimalsSmall, decimalsLarge)</c> (so ordinary sub-1 values like 0.25 or 0.5 - which already fit in
    /// that floor - are unaffected beyond gaining the same precision large-value mantissas already use), and
    /// extended further for smaller magnitudes (0.005, 0.0005, ...) so they never silently round away to "0.00".
    /// </summary>
    static int SmallValueDecimals(double abs, int decimalsSmall, int decimalsLarge)
    {
        int baseline = Math.Max(decimalsSmall, decimalsLarge);
        int leadingZeros = Math.Max(0, -OrderOfMagnitude(abs) - 1);
        return Math.Min(Math.Max(baseline, leadingZeros + 1), MaxSmallValueDecimals);
    }

    /// <summary>
    /// The base-10 exponent of <paramref name="abs"/>'s leading (correctly-rounded) significant digit, e.g. -2
    /// for 0.05, -1 for 0.1 or 0.999. Round-trips through the BCL "E0" formatter rather than
    /// <see cref="Math.Log10(double)"/> so a value that rounds up across a power-of-ten boundary (0.9996 -> "1E+000")
    /// reports the exponent of what will actually be displayed, not a raw-log10 value that floating-point noise
    /// can floor to the wrong side of an exact power of ten.
    /// </summary>
    static int OrderOfMagnitude(double abs)
    {
        string sci = abs.ToString("E0", Inv);
        int eIndex = sci.IndexOf('E');
        return int.Parse(sci.AsSpan(eIndex + 1), NumberStyles.Integer | NumberStyles.AllowLeadingSign, Inv);
    }
}
