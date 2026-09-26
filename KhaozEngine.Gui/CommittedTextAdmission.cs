using System;
using System.Text;

namespace KhaozEngine.Gui
{
    /// <summary>Admits OS committed Unicode scalars into an existing UTF-16 text buffer.</summary>
    internal static class CommittedTextAdmission
    {
        internal static string RemoveLastScalar(string current)
        {
            int length = current.Length;
            if (length == 0) return current;
            if (length > 1 && char.IsLowSurrogate(current[length - 1])
                && char.IsHighSurrogate(current[length - 2]))
                return current[..^2];
            return current[..^1];
        }

        internal static string Append(string current, string text, int maxLength, Func<string, char, bool>? filter,
            bool allowControls = false)
        {
            if (text.Length == 0) return current;
            var result = new StringBuilder(current);
            foreach (Rune rune in text.EnumerateRunes())
            {
                if (!allowControls && rune.Value <= char.MaxValue && char.IsControl((char)rune.Value)) continue;

                string scalar = rune.ToString();
                if (result.Length + scalar.Length > maxLength) continue;
                if (filter != null)
                {
                    string candidate = result.ToString();
                    bool accepted = true;
                    foreach (char unit in scalar)
                    {
                        if (!filter(candidate, unit))
                        {
                            accepted = false;
                            break;
                        }
                        candidate += unit;
                    }
                    if (!accepted) continue;
                }
                result.Append(scalar);
            }
            return result.ToString();
        }
    }
}
