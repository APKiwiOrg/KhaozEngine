using System;

namespace KhaozEngine.UI;

/// <summary>
/// Controls how large numbers are displayed throughout the game.
/// </summary>
public enum NumberNotation
{
    /// <summary>Suffix-based: 1.23K, 45.6M, 7.89B, 1.23T, etc.</summary>
    Simple,

    /// <summary>Scientific notation: 1.23e6, 4.56e12, etc.</summary>
    Scientific,

    /// <summary>Engineering notation: exponents always multiples of 3 (1.23E6, 456E9, etc.).</summary>
    Engineering,
}

/// <summary>
/// Centralized number formatting for all game values (HC, Echoes, damage, HP, costs, etc.).
/// All screens MUST use this instead of local formatting methods.
///
/// Supports three notation modes configurable via <see cref="Notation"/>:
/// Simple (default), Scientific, and Engineering.
///
/// Simple mode uses short suffixes up to Decillions (1e33), then falls back to scientific.
/// </summary>
public static class NumberFormatter
{
    /// <summary>
    /// The current notation mode. Change this to switch all number display globally.
    /// Will be wired to a settings toggle in a future build.
    /// </summary>
    public static NumberNotation Notation { get; set; } = NumberNotation.Simple;

    /// <summary>Suffix table: index 0 = 1e3, index 1 = 1e6, etc.</summary>
    private static readonly string[] Suffixes =
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
    /// Formats a number for display. Uses the current <see cref="Notation"/> mode.
    /// </summary>
    /// <param name="value">The value to format.</param>
    /// <param name="decimalsSmall">Decimal places for values below 1,000 (default 1).</param>
    /// <param name="decimalsLarge">Decimal places for suffixed/scientific values (default 2).</param>
    public static string Format(double value, int decimalsSmall = 1, int decimalsLarge = 2)
    {
        if (double.IsNaN(value)) return "0";
        if (double.IsInfinity(value)) return "Inf";

        bool negative = value < 0;
        double abs = Math.Abs(value);
        string prefix = negative ? "-" : "";

        return Notation switch
        {
            NumberNotation.Simple => prefix + FormatSimple(abs, decimalsSmall, decimalsLarge),
            NumberNotation.Scientific => prefix + FormatScientific(abs, decimalsSmall, decimalsLarge),
            NumberNotation.Engineering => prefix + FormatEngineering(abs, decimalsSmall, decimalsLarge),
            _ => prefix + FormatSimple(abs, decimalsSmall, decimalsLarge),
        };
    }

    /// <summary>
    /// Formats a number for display with zero decimal places for small values.
    /// Convenience overload for integer-like values (ore counts, depths, levels).
    /// </summary>
    public static string FormatInt(double value)
    {
        return Format(value, decimalsSmall: 0, decimalsLarge: 2);
    }

    private static string FormatSimple(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (abs < 1000)
            return abs.ToString($"F{decimalsSmall}");

        string largeFmt = $"F{decimalsLarge}";

        // Walk the suffix table
        double threshold = 1000.0;
        for (int i = 0; i < Suffixes.Length; i++)
        {
            double nextThreshold = threshold * 1000.0;
            if (abs < nextThreshold)
                return (abs / threshold).ToString(largeFmt) + Suffixes[i];
            threshold = nextThreshold;
        }

        // Beyond suffix table: fall back to scientific
        return abs.ToString($"E{decimalsLarge}");
    }

    private static string FormatScientific(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (abs < 1000)
            return abs.ToString($"F{decimalsSmall}");

        return abs.ToString($"E{decimalsLarge}");
    }

    private static string FormatEngineering(double abs, int decimalsSmall, int decimalsLarge)
    {
        if (abs < 1000)
            return abs.ToString($"F{decimalsSmall}");

        // Engineering notation: exponent is always a multiple of 3
        int exp = (int)Math.Floor(Math.Log10(abs));
        int engExp = exp - exp % 3;
        double mantissa = abs / Math.Pow(10, engExp);

        return mantissa.ToString($"F{decimalsLarge}") + "E" + engExp;
    }
}
