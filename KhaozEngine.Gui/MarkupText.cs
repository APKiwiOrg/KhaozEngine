using System;
using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>
/// Opt-in localized inline markup. Catalog templates are trusted to carry semantic tags. Format arguments are
/// culture-formatted and escaped before insertion, so caller or player text cannot introduce tags.
/// </summary>
public readonly struct MarkupText
{
    readonly StringId _id;
    readonly object?[]? _escapedArgs;

    MarkupText(StringId id, object?[]? args)
    {
        _id = id;
        _escapedArgs = EscapeArguments(args);
    }

    /// <summary>Build trusted localized markup from a catalog key and safely escaped format arguments.</summary>
    public static MarkupText Of(StringId id, params object?[] args) => new(id, args);

    internal string ResolveSource()
    {
        if (_id.Key is null) return "";
        IStringCatalog? catalog = LocalizationContext.Catalog;
        if (catalog is null) return _id.Key;
        return _escapedArgs is { Length: > 0 }
            ? catalog.Format(_id.Key, _escapedArgs)
            : catalog.Get(_id.Key);
    }

    static object?[]? EscapeArguments(object?[]? args)
    {
        if (args is not { Length: > 0 }) return args;
        var escaped = new object?[args.Length];
        for (int i = 0; i < args.Length; i++) escaped[i] = new EscapedArgument(args[i]);
        return escaped;
    }

    sealed class EscapedArgument : IFormattable
    {
        readonly object? _value;

        internal EscapedArgument(object? value) => _value = value;

        public string ToString(string? format, IFormatProvider? formatProvider)
        {
            string formatted = _value switch
            {
                null => "",
                IFormattable formattable => formattable.ToString(format, formatProvider) ?? "",
                _ => _value.ToString() ?? "",
            };
            return InlineMarkup.Escape(formatted);
        }

        public override string ToString() => ToString(null, null);
    }
}
