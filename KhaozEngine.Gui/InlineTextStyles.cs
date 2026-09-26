using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Gui;

/// <summary>One semantic inline-text style name and the colour a caller's current theme assigns it.</summary>
public readonly record struct InlineTextStyle(string Name, Vector4 Color);

/// <summary>
/// An immutable, ordinal mapping from semantic inline-text style names to theme colours. A name absent from the
/// map inherits its enclosing colour when markup resolves.
/// </summary>
public sealed class InlineTextStyles
{
    readonly Dictionary<string, Vector4> _colors;

    /// <summary>A style map with no overrides, so every semantic span inherits its enclosing colour.</summary>
    public static InlineTextStyles Empty { get; } = new();

    /// <summary>Copy <paramref name="styles"/> into an immutable ordinal map.</summary>
    /// <exception cref="ArgumentException">A name is invalid or appears more than once.</exception>
    public InlineTextStyles(params InlineTextStyle[] styles)
    {
        ArgumentNullException.ThrowIfNull(styles);
        _colors = new Dictionary<string, Vector4>(styles.Length, StringComparer.Ordinal);
        for (int i = 0; i < styles.Length; i++)
        {
            InlineTextStyle style = styles[i];
            if (!InlineMarkup.IsStyleName(style.Name.AsSpan()))
                throw new ArgumentException($"'{style.Name}' is not a valid inline text style name.", nameof(styles));
            if (!_colors.TryAdd(style.Name, style.Color))
                throw new ArgumentException($"Inline text style '{style.Name}' appears more than once.", nameof(styles));
        }
    }

    internal bool TryGetColor(string name, out Vector4 color) => _colors.TryGetValue(name, out color);
}
