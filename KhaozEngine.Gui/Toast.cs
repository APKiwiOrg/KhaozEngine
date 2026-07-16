using KhaozEngine.App;

namespace KhaozEngine.Gui;

/// <summary>Visual/semantic category of a <see cref="Toast"/>, used by the theme to pick a colour.</summary>
public enum ToastKind
{
    Standard,
    Warning,
    Danger,
}

/// <summary>
/// A single retained toast notification tracked by a <see cref="ToastStack"/>. Immutable from the outside except
/// for <see cref="Remaining"/>, which the owning stack counts down each <see cref="ToastStack.Update"/>.
/// </summary>
public sealed class Toast
{
    /// <summary>The message text, resolved through the localization catalog when drawn.</summary>
    public LocalizedText Message { get; init; }

    /// <summary>The visual/semantic category.</summary>
    public ToastKind Kind { get; init; }

    /// <summary>
    /// Total lifetime in seconds from the moment the toast was shown. A value <c>&lt;= 0</c> means sticky
    /// (never expires on its own, see <see cref="IsSticky"/>).
    /// </summary>
    public float Duration { get; init; }

    /// <summary>
    /// Seconds left before the toast expires. Counted down by <see cref="ToastStack.Update"/>. Meaningless for a
    /// sticky toast, which is never decremented.
    /// </summary>
    public float Remaining { get; internal set; }

    /// <summary>
    /// The replacement channel. Two toasts sharing the same non-null key replace each other in place (see
    /// <see cref="ToastStack.Show"/>). <c>null</c> means unkeyed: the toast never gets replaced by key.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>True when <see cref="Duration"/> is <c>&lt;= 0</c>, meaning this toast never expires on its own.</summary>
    public bool IsSticky => Duration <= 0f;
}
