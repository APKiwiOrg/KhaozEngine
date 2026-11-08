using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The two text intents. The numbering is durable, because a provider stores it.
/// </summary>
public enum ContentTextEditOperation
{
    /// <summary>The string takes a complete value, which may be empty.</summary>
    Set = 1,

    /// <summary>The string is absent, which lets a reader fall back.</summary>
    Remove = 2,
}

/// <summary>
/// One text intent against one <see cref="ContentTextTarget"/>: a Set carrying a complete value, or a Remove
/// carrying none. <c>Set("")</c> is a PRESENT empty value and is not a Remove.
/// <para>
/// Values are stored exactly as submitted. They are not trimmed, case folded, interpolated or Unicode
/// normalized. They are measured as strict UTF-8, so an unpaired surrogate is refused rather than replaced,
/// and a value above <see cref="MaxValueBytes"/> bytes is refused whatever its character count.
/// </para>
/// </summary>
public sealed record ContentTextEdit
{
    /// <summary>The largest value in UTF-8 bytes.</summary>
    public const int MaxValueBytes = 8192;

    ContentTextEdit(ContentTextTarget target, ContentTextEditOperation operation, string? value)
    {
        Target = target;
        Operation = operation;
        Value = value;
    }

    /// <summary>The string the intent names.</summary>
    public ContentTextTarget Target { get; }

    /// <summary>Set or Remove.</summary>
    public ContentTextEditOperation Operation { get; }

    /// <summary>The complete value of a Set, and null on a Remove.</summary>
    public string? Value { get; }

    /// <summary>A complete value for one string.</summary>
    /// <param name="target">The string.</param>
    /// <param name="value">The value, empty allowed, at most <see cref="MaxValueBytes"/> strict UTF-8 bytes.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The value carries an unpaired surrogate or exceeds the byte bound.</exception>
    public static ContentTextEdit Set(ContentTextTarget target, string value)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ContentTextEdit(target, ContentTextEditOperation.Set, RequireValue(value, nameof(value)));
    }

    /// <summary>The string made absent.</summary>
    /// <param name="target">The string.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    public static ContentTextEdit Remove(ContentTextTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ContentTextEdit(target, ContentTextEditOperation.Remove, null);
    }

    /// <summary>A complete value checked as strict UTF-8 of at most <see cref="MaxValueBytes"/> bytes.</summary>
    /// <param name="value">The value.</param>
    /// <param name="parameterName">The argument a refusal names.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException">The value is invalid UTF-8 or too long.</exception>
    internal static string RequireValue(string value, string parameterName)
    {
        if (FindValueDefect(value, parameterName) is { } finding) throw finding.ToException();
        return value;
    }

    /// <summary>The first Unicode or byte-bound rule a value breaks.</summary>
    internal static ContentTextFinding? FindValueDefect(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        int bytes = MeasureUtf8(value, parameterName, out ContentTextFinding? malformed);
        return malformed ?? (bytes > MaxValueBytes
            ? new ContentTextFinding(ContentAuthoringRefusal.TextValueBytesExceeded,
                FormattableString.Invariant(
                    $"A text value is at most {MaxValueBytes} UTF-8 bytes and this one is {bytes}."), parameterName)
                { MemberName = "value", ActualBytes = bytes, MaximumBytes = MaxValueBytes }
            : null);
    }

    /// <summary>
    /// The strict UTF-8 length of <paramref name="value"/>, refusing an unpaired surrogate instead of counting
    /// a replacement character for it. Checked arithmetic, so no length can wrap.
    /// </summary>
    /// <param name="value">The string to measure.</param>
    /// <param name="parameterName">The argument a refusal names.</param>
    /// <exception cref="ArgumentException">The string carries an unpaired surrogate.</exception>
    internal static int MeasureUtf8(string value, string parameterName)
    {
        int bytes = MeasureUtf8(value, parameterName, out ContentTextFinding? finding);
        if (finding is not null) throw finding.ToException();
        return bytes;
    }

    internal static int MeasureUtf8(string value, string parameterName, out ContentTextFinding? finding)
    {
        finding = null;
        int bytes = 0;
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (char.IsHighSurrogate(current))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    finding = Unpaired(i, parameterName);
                    return bytes;
                }

                bytes = checked(bytes + 4);
                i++;
            }
            else if (char.IsLowSurrogate(current))
            {
                finding = Unpaired(i, parameterName);
                return bytes;
            }
            else
            {
                bytes = checked(bytes + (current < 0x80 ? 1 : current < 0x800 ? 2 : 3));
            }
        }

        return bytes;
    }

    static ContentTextFinding Unpaired(int index, string parameterName)
        => new(ContentAuthoringRefusal.TextMalformedUnicode,
            FormattableString.Invariant(
                $"Text carries an unpaired surrogate at index {index}, which is not valid UTF-8 and is refused rather than replaced."),
            parameterName) { MemberName = "value", CharacterIndex = index };
}
