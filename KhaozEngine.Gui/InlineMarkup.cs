using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using KhaozEngine.Render2D;

namespace KhaozEngine.Gui;

/// <summary>Resolves trusted localized semantic colour markup into the renderer's coloured text runs.</summary>
public static class InlineMarkup
{
    /// <summary>
    /// Resolve <paramref name="text"/> through <paramref name="styles"/>. Missing style names inherit their
    /// enclosing colour. Malformed markup renders as one literal run at <paramref name="defaultColor"/>.
    /// </summary>
    public static ColoredTextRun[] Resolve(MarkupText text, InlineTextStyles styles, Vector4 defaultColor)
    {
        ArgumentNullException.ThrowIfNull(styles);
        string source = text.ResolveSource();
        if (source.Length == 0) return Array.Empty<ColoredTextRun>();
        return TryParse(source, styles, defaultColor, out ColoredTextRun[]? runs)
            ? runs
            : [new ColoredTextRun(source, defaultColor)];
    }

    // MarkupText is the only formatting door. Keeping this internal prevents callers from assembling trusted
    // markup out of already-formatted strings and accidentally bypassing its all-arguments-are-literal rule.
    internal static string Escape(string? literal) =>
        string.IsNullOrEmpty(literal) || literal.IndexOf('[', StringComparison.Ordinal) < 0
            ? literal ?? ""
            : literal.Replace("[", "[[", StringComparison.Ordinal);

    static bool TryParse(string source, InlineTextStyles styles, Vector4 defaultColor,
        out ColoredTextRun[] runs)
    {
        var parsed = new List<ColoredTextRun>();
        var colors = new List<Vector4> { defaultColor };
        var text = new StringBuilder();
        int at = 0;
        while (at < source.Length)
        {
            if (source[at] != '[')
            {
                text.Append(source[at++]);
                continue;
            }

            if (at + 1 < source.Length && source[at + 1] == '[')
            {
                text.Append('[');
                at += 2;
                continue;
            }

            if (at + 2 < source.Length && source[at + 1] == '/' && source[at + 2] == ']')
            {
                if (colors.Count == 1)
                {
                    runs = Array.Empty<ColoredTextRun>();
                    return false;
                }
                Flush(parsed, text, colors[^1]);
                colors.RemoveAt(colors.Count - 1);
                at += 3;
                continue;
            }

            int close = source.IndexOf(']', at + 1);
            if (close < 0 || !IsStyleName(source.AsSpan(at + 1, close - at - 1)))
            {
                runs = Array.Empty<ColoredTextRun>();
                return false;
            }

            Flush(parsed, text, colors[^1]);
            string name = source[(at + 1)..close];
            colors.Add(styles.TryGetColor(name, out Vector4 mapped) ? mapped : colors[^1]);
            at = close + 1;
        }

        if (colors.Count != 1)
        {
            runs = Array.Empty<ColoredTextRun>();
            return false;
        }

        Flush(parsed, text, colors[0]);
        runs = parsed.ToArray();
        return true;
    }

    static void Flush(List<ColoredTextRun> runs, StringBuilder text, Vector4 color)
    {
        if (text.Length == 0) return;
        string piece = text.ToString();
        text.Clear();
        if (runs.Count > 0 && runs[^1].Color == color)
        {
            ColoredTextRun previous = runs[^1];
            runs[^1] = previous with { Text = previous.Text + piece };
        }
        else
        {
            runs.Add(new ColoredTextRun(piece, color));
        }
    }

    internal static bool IsStyleName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || !IsAsciiLetter(name[0])) return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!IsAsciiLetter(c) && !char.IsAsciiDigit(c) && c != '.' && c != '_' && c != '-') return false;
        }
        return true;
    }

    static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
}
