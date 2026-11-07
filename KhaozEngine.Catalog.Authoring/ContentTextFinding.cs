using System;

namespace KhaozEngine.Catalog.Authoring;

/// <summary>
/// A text constructor's first refused rule. Constructors keep their plain argument exceptions, and the
/// bundle reader maps this finding with the caught exception as its inner, without reading its words.
/// </summary>
internal sealed record ContentTextFinding(ContentAuthoringRefusal Refusal, string Message, string ParameterName)
{
    public string? CollectionName { get; init; }
    public int? Index { get; init; }
    public string? MemberName { get; init; }
    public string? LanguageTag { get; init; }
    public string? Language { get; init; }
    public string? WireTag { get; init; }
    public ContentTextTarget? TextTarget { get; init; }
    public long? ActualBytes { get; init; }
    public long? MaximumBytes { get; init; }

    /// <summary>The constructor's original argument refusal, including its parameter suffix.</summary>
    public ArgumentException ToException() => new(Message, ParameterName);

    /// <summary>The same rule and identifiers under the bundle reader's context.</summary>
    public ContentAuthoringException ToBundleException(string path, string prefix, ArgumentException malformed)
    {
        if (CollectionName is string collection) path = ContentBundleJson.Member(path, collection);
        if (Index is int index) path = ContentBundleJson.Element(path, index);
        if (MemberName is string member) path = ContentBundleJson.Member(path, member);
        return new(Refusal, prefix + malformed.Message, default, 0, ContentAuthoringException.BundleFormatReason, malformed)
        {
            JsonPath = path,
            LanguageTag = LanguageTag,
            Language = Language,
            WireTag = WireTag,
            TextTarget = TextTarget,
            ActualBytes = ActualBytes,
            MaximumBytes = MaximumBytes,
        };
    }
}
