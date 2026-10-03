namespace KhaozEngine.Server.Admin.Catalog;

/// <summary>
/// One pending TEXT intent, expanded, as <c>catalog-draft</c> lists it. It names its row by KEY, because a
/// pending add has no id until publish, and it carries the derived localization key a client resolves.
/// </summary>
/// <param name="Op"><c>set</c> or <c>remove</c>, the vocabulary a <c>textEdits</c> entry uses.</param>
/// <param name="TypeKey">The content type's key.</param>
/// <param name="Key">The row's content key.</param>
/// <param name="Field">The localized text marker field.</param>
/// <param name="Language">The canonical, lowered language identity.</param>
/// <param name="Value">The complete value of a set, empty allowed, and null on a remove.</param>
/// <param name="DerivedKey">The localization key the value resolves under.</param>
public sealed record CatalogDraftTextEditPayload(
    string Op,
    string TypeKey,
    string Key,
    string Field,
    string Language,
    string? Value,
    string DerivedKey);

/// <summary>
/// One language a draft introduces or a publish introduced: its canonical identity and the exact spelling a
/// manifest carries it under.
/// </summary>
/// <param name="Language">The canonical, lowered identity.</param>
/// <param name="WireTag">The spelling a manifest names it by.</param>
public sealed record CatalogTextLanguagePayload(string Language, string WireTag);

/// <summary>One published string of one row, as <c>catalog-get</c> lists it.</summary>
/// <param name="Field">The localized text marker field.</param>
/// <param name="Language">The canonical language identity.</param>
/// <param name="Value">The complete value, empty allowed.</param>
/// <param name="DerivedKey">The localization key the value resolves under.</param>
/// <param name="ValidFrom">The version the value was published in.</param>
public sealed record CatalogTextValuePayload(
    string Field,
    string Language,
    string Value,
    string DerivedKey,
    int ValidFrom);

/// <summary>
/// One string's before and after in a diff. A null side is ABSENT, which lets a reader fall back, and is not
/// the empty string, which is a present value.
/// </summary>
/// <param name="Type">The content type's key.</param>
/// <param name="Id">The definition id, 0 for a row the draft adds, because publish allocates.</param>
/// <param name="Key">The row's content key.</param>
/// <param name="Field">The localized text marker field.</param>
/// <param name="Language">The canonical language identity.</param>
/// <param name="Before">The source value, or null when absent there.</param>
/// <param name="After">The destination value, or null when absent there.</param>
/// <param name="DerivedKey">The localization key the value resolves under.</param>
public sealed record CatalogTextChangePayload(
    string Type,
    int Id,
    string Key,
    string Field,
    string Language,
    string? Before,
    string? After,
    string DerivedKey);
