using System;

namespace KhaozEngine.Catalog;

/// <summary>The rules <see cref="ContentFieldSchema"/> checks each entry against, in the order it checks them.</summary>
internal enum ContentFieldSchemaRule
{
    /// <summary>The entry is null.</summary>
    EntryNull,

    /// <summary>The entry's name is empty or white space.</summary>
    NameBlank,

    /// <summary>A key reference names no content type key.</summary>
    KeyReferenceTargetMissing,

    /// <summary>A tag list names a reference target other than <see cref="ContentFieldEntry.TagReferenceTarget"/>.</summary>
    TagReferenceTargetInvalid,

    /// <summary>A kind that is neither a key reference nor a tag list carries a reference target.</summary>
    ReferenceTargetUnexpected,

    /// <summary>A scaled int declares a scale below 1.</summary>
    ScaleBelowOne,

    /// <summary>A kind other than a scaled int declares a scale other than 1.</summary>
    ScaleNotOne,

    /// <summary>The entry repeats an earlier entry's name.</summary>
    NameDuplicate,

    /// <summary>An entry appended after the baseline is required.</summary>
    AppendedFieldRequired,
}

/// <summary>
/// The first rule a field list breaks, typed. <see cref="ContentFieldSchema"/> throws it as the plain
/// <see cref="ArgumentException"/> or <see cref="ArgumentOutOfRangeException"/> its callers catch, and the bundle
/// reader in <c>KhaozEngine.Catalog.Authoring</c> maps <see cref="Rule"/> to its own refusal without parsing the
/// message.
/// </summary>
internal sealed class ContentFieldSchemaFinding
{
    internal ContentFieldSchemaFinding(ContentFieldSchemaRule rule, int index, ContentFieldEntry? entry, string message)
    {
        Rule = rule;
        Index = index;
        Entry = entry;
        Message = message;
    }

    /// <summary>The rule the entry breaks.</summary>
    public ContentFieldSchemaRule Rule { get; }

    /// <summary>The entry's index in the field list.</summary>
    public int Index { get; }

    /// <summary>The entry that breaks the rule, null only for <see cref="ContentFieldSchemaRule.EntryNull"/>.</summary>
    public ContentFieldEntry? Entry { get; }

    /// <summary>The explanation, without the parameter suffix the BCL exception appends.</summary>
    public string Message { get; }

    /// <summary>The exception <see cref="ContentFieldSchema"/> throws for this finding.</summary>
    /// <param name="parameterName">The argument the exception names.</param>
    public ArgumentException ToException(string parameterName)
        => Rule is ContentFieldSchemaRule.ScaleBelowOne or ContentFieldSchemaRule.ScaleNotOne
            ? new ArgumentOutOfRangeException(parameterName, Entry!.Scale, Message)
            : new ArgumentException(Message, parameterName);
}
