using System;
using System.Collections.Generic;
using System.Text.Json;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// The TEXT SECTION of a format 2 bundle document, written and read here and nowhere else.
/// <para>
/// The section is one object after every row member: <c>languages</c>, each with its canonical identity and
/// exact wire spelling, empty languages included, then <c>values</c>, each targeting a type id, content key,
/// marker field and canonical language with its complete value. A value never names a raw derived key,
/// because the key is a function of the other three.
/// </para>
/// <para>
/// Reading is total for shape, like the rest of the document. A missing section, two declarations or values
/// whose languages share a canonical identity, a malformed target and an invalid value are each a refusal
/// naming what was wrong, never a partial section.
/// </para>
/// </summary>
internal static class ContentBundleTextJson
{
    /// <summary>The document member the section is written under.</summary>
    public const string SectionName = "text";

    /// <summary>Writes the section in a fixed member order, so two exports of one version are the same bytes.</summary>
    /// <param name="writer">The document writer, positioned inside the root object.</param>
    /// <param name="text">The section.</param>
    public static void Write(Utf8JsonWriter writer, ContentBundleTextState text)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(text);

        writer.WriteStartObject(SectionName);
        writer.WriteStartArray("languages");
        foreach (ContentTextLanguageDeclaration language in text.Languages)
        {
            writer.WriteStartObject();
            writer.WriteString("language", language.Language);
            writer.WriteString("wireTag", language.WireTag);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("values");
        foreach (ContentBundleTextValue value in text.Values)
        {
            writer.WriteStartObject();
            writer.WriteNumber("typeId", value.Target.Type.Value);
            writer.WriteString("key", value.Target.Key.ToString());
            writer.WriteString("field", value.Target.FieldName);
            writer.WriteString("language", value.Target.Language);
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Reads the section a format 2 document carries.</summary>
    /// <param name="root">The document's root object.</param>
    /// <exception cref="ContentAuthoringException">The section is missing or malformed, with <see cref="ContentAuthoringException.BundleFormatReason"/>.</exception>
    public static ContentBundleTextState Read(JsonElement root)
    {
        if (!root.TryGetProperty(SectionName, out JsonElement section) || section.ValueKind != JsonValueKind.Object)
        {
            throw ContentBundleJson.Refuse(FormattableString.Invariant(
                $"A format {ContentBundle.TextFormatVersion} bundle carries a '{SectionName}' object, and this one does not. A bundle that lost its text section is never read as text free."));
        }

        var languages = new List<ContentTextLanguageDeclaration>();
        foreach (JsonElement element in ContentBundleJson.Array(section, "languages"))
        {
            string language = StrictText(element, "language");
            string wireTag = StrictText(element, "wireTag");
            languages.Add(Shaped(
                () => new ContentTextLanguageDeclaration(language, wireTag), "A bundle text language is malformed: "));
        }

        var values = new List<ContentBundleTextValue>();
        foreach (JsonElement element in ContentBundleJson.Array(section, "values"))
        {
            ContentTypeId type = ContentBundleJson.TypeId(element, "typeId");
            string key = StrictText(element, "key");
            string field = StrictText(element, "field");
            string language = StrictText(element, "language");
            string value = StrictText(element, "value");
            values.Add(Shaped(
                () => new ContentBundleTextValue(new ContentTextTarget(type, new ContentKey(key), field, language), value),
                "A bundle text value is malformed: "));
        }

        // Two declarations or two values whose languages share a canonical identity are refused here, even
        // when their values agree, because one identity has one spelling and one value per string.
        return Shaped(() => new ContentBundleTextState(languages, values), "A bundle text section is malformed: ");
    }

    /// <summary>A string member read without replacing an unpaired surrogate escape.</summary>
    static string StrictText(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement element) || element.ValueKind != JsonValueKind.String)
        {
            throw ContentBundleJson.Refuse(FormattableString.Invariant(
                $"A bundle member '{name}' is a string, and this one is not."));
        }

        try
        {
            return element.GetString()!;
        }
        catch (InvalidOperationException invalid)
        {
            throw ContentBundleJson.Refuse(FormattableString.Invariant(
                $"A bundle member '{name}' is valid text, and this one is not: {invalid.Message}"));
        }
    }

    /// <summary>One construction whose argument refusal becomes the document's own refusal.</summary>
    static T Shaped<T>(Func<T> build, string prefix)
    {
        try
        {
            return build();
        }
        catch (ArgumentException malformed)
        {
            throw ContentBundleJson.Refuse(prefix + malformed.Message);
        }
    }
}
