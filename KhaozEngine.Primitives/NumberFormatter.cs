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
            return abs.ToString("F" + decimalsSmall.ToString(Inv), Inv);

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
            return abs.ToString("F" + decimalsSmall.ToString(Inv), Inv);

        return abs.ToString("E" + decimalsLarge.ToString(Inv), Inv);
    }

    static string FormatEngineering(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (abs < 1000)
            return abs.ToString("F" + decimalsSmall.ToString(Inv), Inv);

        // Engineering notation: exponent is always a multiple of 3.
        int exp = (int)Math.Floor(Math.Log10(abs));
        int engExp = exp - exp % 3;
        double mantissa = abs / Math.Pow(10, engExp);

        return mantissa.ToString("F" + decimalsLarge.ToString(Inv), Inv) + "E" + engExp.ToString(Inv);
    }
}
