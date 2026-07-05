namespace KhaozEngine.App;

/// <summary>
/// A piece of player-facing text that resolves lazily against the current locale. It is EITHER a localizable
/// <see cref="StringId"/> (with optional format args) OR a raw literal (<see cref="Raw"/>) for non-localizable
/// tokens - names, numbers, debug text. Gui sinks accept <see cref="LocalizedText"/> instead of
/// <see cref="string"/>, and the only implicit conversion is from <see cref="StringId"/> (never from
/// <see cref="string"/>), so a bare string literal at a sink fails to compile. The value stores the id + args
/// and re-resolves on every <see cref="Resolve()"/> - it never caches - so a runtime locale switch takes effect
/// on the next draw.
/// </summary>
public readonly struct LocalizedText
{
    private readonly StringId _id;
    private readonly object?[]? _args;
    private readonly string? _raw;
    private readonly bool _isRaw;

    private LocalizedText(StringId id, object?[]? args)
    {
        _id = id;
        _args = args;
        _raw = null;
        _isRaw = false;
    }

    private LocalizedText(string raw)
    {
        _id = default;
        _args = null;
        _raw = raw;
        _isRaw = true;
    }

    /// <summary>True when this is a raw literal (see <see cref="Raw"/>), false when it is a localizable key.</summary>
    public bool IsRaw => _isRaw;

    /// <summary>The underlying key when this is localizable (default when <see cref="IsRaw"/>).</summary>
    public StringId Id => _id;

    /// <summary>A localizable value from a key with no format args (implicit at the call site).</summary>
    public static implicit operator LocalizedText(StringId id) => new(id, null);

    /// <summary>A localizable value from a key with format args - resolved via <see cref="IStringCatalog.Format"/>.</summary>
    public static LocalizedText Of(StringId id, params object?[] args) => new(id, args);

    /// <summary>
    /// The escape hatch: text that is intentionally NOT localized (a proper name, a number, debug text). The
    /// literal token <c>LocalizedText.Raw</c> is greppable, and the analyzer flags it outside exempt/debug code.
    /// </summary>
    public static LocalizedText Raw(string text) => new(text ?? "");

    /// <summary>
    /// Resolve against an explicit catalog. Raw text returns verbatim. A localizable value resolves via
    /// <see cref="IStringCatalog.Get"/> (or <see cref="IStringCatalog.Format"/> when it has args); with a null
    /// catalog it returns the key as a visible placeholder. <c>default</c> resolves to the empty string.
    /// </summary>
    public string Resolve(IStringCatalog? catalog)
    {
        if (_isRaw) return _raw ?? "";
        if (_id.Key is null) return ""; // default(LocalizedText)
        if (catalog is null) return _id.Key;
        return _args is { Length: > 0 } ? catalog.Format(_id.Key, _args) : catalog.Get(_id.Key);
    }

    /// <summary>Resolve against the ambient <see cref="LocalizationContext.Catalog"/>.</summary>
    public string Resolve() => Resolve(LocalizationContext.Catalog);

    /// <summary>Convenience: resolves against the ambient catalog (for logs/debug).</summary>
    public override string ToString() => Resolve();
}
