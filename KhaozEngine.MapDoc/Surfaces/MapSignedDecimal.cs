using System.Globalization;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>The canonical signed int64 decimal text shared by the topology and water codecs: an optional leading
/// minus, no plus sign, no leading zeros and no negative zero, over the full signed range.</summary>
internal static class MapSignedDecimal
{
    internal static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal static bool TryParse(string? text, out long value)
    {
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) && text == Format(value))
            return true;
        value = 0;
        return false;
    }
}
