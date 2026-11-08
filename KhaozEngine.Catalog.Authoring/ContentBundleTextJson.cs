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
        string sectionPath = ContentBundleJson.Member(ContentBundleJson.RootPath, SectionName);
        bool present = root.TryGetProperty(SectionName, out JsonElement section);
        if (!present || section.ValueKind != JsonValueKind.Object)
        {
            throw ContentBundleJson.Shape(present, section, sectionPath, FormattableString.Invariant(
                $"A format {ContentBundle.TextFormatVersion} bundle carries a '{SectionName}' object, and this one does not. A bundle that lost its text section is never read as text free."));
        }

        string languagesPath = ContentBundleJson.Member(sectionPath, "languages");
        int languageIndex = 0;
        var languages = new List<ContentTextLanguageDeclaration>();
        foreach (JsonElement element in ContentBundleJson.Array(section, sectionPath, "languages"))
        {
            string languagePath = ContentBundleJson.Element(languagesPath, languageIndex++);
            string language = StrictText(element, languagePath, "language");
            string wireTag = StrictText(element, languagePath, "wireTag");
            languages.Add(Shaped(
                () => new ContentTextLanguageDeclaration(language, wireTag),
                () => ContentTextLanguageDeclaration.FindDefect(language, wireTag),
                languagePath, "A bundle text language is malformed: "));
        }

        string valuesPath = ContentBundleJson.Member(sectionPath, "values");
        int valueIndex = 0;
        var values = new List<ContentBundleTextValue>();
        foreach (JsonElement element in ContentBundleJson.Array(section, sectionPath, "values"))
        {
            string valuePath = ContentBundleJson.Element(valuesPath, valueIndex++);
            ContentTypeId type = ContentBundleJson.TypeId(element, valuePath, "typeId");
            string key = StrictText(element, valuePath, "key");
            string field = StrictText(element, valuePath, "field");
            string language = StrictText(element, valuePath, "language");
            string value = StrictText(element, valuePath, "value");
            values.Add(Shaped(
                () => new ContentBundleTextValue(new ContentTextTarget(type, new ContentKey(key), field, language), value),
                () => ContentTextTarget.FindDefect(new ContentKey(key), field, language) ?? ContentTextEdit.FindValueDefect(value, nameof(value)),
                valuePath, "A bundle text value is malformed: "));
        }

        // Two declarations or two values whose languages share a canonical identity are refused here, even
        // when their values agree, because one identity has one spelling and one value per string.
        return Shaped(() => new ContentBundleTextState(languages, values),
            () => ContentBundleTextState.FindDefect(languages, values), sectionPath, "A bundle text section is malformed: ");
    }

    /// <summary>A string member read without replacing an unpaired surrogate escape.</summary>
    static string StrictText(JsonElement parent, string parentPath, string name)
    {
        bool present = parent.TryGetProperty(name, out JsonElement element);
        if (!present || element.ValueKind != JsonValueKind.String)
        {
            throw ContentBundleJson.Shape(present, element, ContentBundleJson.Member(parentPath, name), FormattableString.Invariant(
                $"A bundle member '{name}' is a string, and this one is not."));
        }

        try
        {
            return element.GetString()!;
        }
        catch (InvalidOperationException invalid)
        {
            throw new ContentAuthoringException(ContentAuthoringRefusal.TextMalformedUnicode, FormattableString.Invariant(
                $"A bundle member '{name}' is valid text, and this one is not: {invalid.Message}"),
                default, 0, ContentAuthoringException.BundleFormatReason, invalid)
            { JsonPath = ContentBundleJson.Member(parentPath, name) };
        }
    }

    /// <summary>One construction whose argument refusal becomes the document's own refusal.</summary>
    static T Shaped<T>(Func<T> build, Func<ContentTextFinding?> findDefect, string path, string prefix)
    {
        try
        {
            return build();
        }
        catch (ArgumentException malformed)
        {
            // All strings and entries were read above, so null arguments cannot reach these constructors.
            // StrictText already refuses unpaired surrogate escapes before a value constructor can see them.
            ContentTextFinding finding = findDefect()
                ?? throw new ContentAuthoringException(ContentAuthoringRefusal.BundleConstructorRuleUnmapped,
                    prefix + malformed.Message, default, 0, ContentAuthoringException.BundleFormatReason, malformed);
            throw finding.ToBundleException(path, prefix, malformed);
        }
    }
}
