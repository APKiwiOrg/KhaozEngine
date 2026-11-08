using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.Catalog;

/// <summary>
/// Thrown by <see cref="ContentTypeRegistry.RegisterContentType"/> when a registration breaks one of the
/// rules of contracts 4.2 to 4.5 and 4.7: a reserved type id, a band the caller is not entitled to, a
/// duplicate id or key, an illegal chunk slot count or row cap, a chunk bound no reader could load, or a
/// codec whose written fields differ from its schema. Also thrown when a snapshot row has an unregistered
/// type. Reason and typed identifiers describe the refused rule. The message is developer prose.
/// <para>
/// It THROWS rather than returning a result, deliberately: registration runs once at process start, before
/// any pack loads, so the failure belongs on the line that caused it rather than in a report nobody reads.
/// </para>
/// </summary>
public sealed class ContentRegistrationException : Exception
{
    readonly IReadOnlyList<string>? _fieldNames;
    readonly IReadOnlyList<string>? _missingFields;
    readonly IReadOnlyList<string>? _extraFields;

    /// <summary>Creates the exception with no message.</summary>
    public ContentRegistrationException()
    {
    }

    /// <summary>Creates a consumer exception with a developer-facing explanation.</summary>
    public ContentRegistrationException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public ContentRegistrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Creates a refusal with its rule and developer-facing explanation.</summary>
    public ContentRegistrationException(ContentRegistrationRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>Creates a refusal with its rule, developer-facing explanation and inner cause.</summary>
    public ContentRegistrationException(ContentRegistrationRefusal reason, string message, Exception innerException)
        : base(message, innerException) => Reason = reason;

    /// <summary>The refused rule. Null only for an instance consumer code creates through a legacy
    /// constructor without a reason. Engine throw sites always supply a reason.</summary>
    public ContentRegistrationRefusal? Reason { get; }

    /// <summary>The type id being registered or admitted to a snapshot.</summary>
    public ContentTypeId? Type { get; init; }

    /// <summary>The submitted type key for a registration refusal, including an empty key.</summary>
    public string? TypeKey { get; init; }

    /// <summary>The submitted band for an unrecognized band or out-of-band type id.</summary>
    public ContentRegistrationBand? Band { get; init; }

    /// <summary>The recognized band's inclusive lower type id.</summary>
    public ushort? MinimumTypeId { get; init; }

    /// <summary>The recognized band's inclusive upper type id.</summary>
    public ushort? MaximumTypeId { get; init; }

    /// <summary>The key already registered for a duplicate type id.</summary>
    public string? ExistingTypeKey { get; init; }

    /// <summary>The id already registered for a duplicate type key.</summary>
    public ContentTypeId? ExistingTypeId { get; init; }

    /// <summary>The refused chunk slot count.</summary>
    public int? ActualSlots { get; init; }

    /// <summary>The inclusive chunk slot floor, 256, for either slot-count refusal.</summary>
    public int? MinimumSlots { get; init; }

    /// <summary>The inclusive chunk slot ceiling, 65,536, for either slot-count refusal.</summary>
    public int? MaximumSlots { get; init; }

    /// <summary>The refused row cap or calculated worst-case uncompressed chunk size, in bytes.</summary>
    public long? ActualBytes { get; init; }

    /// <summary>The inclusive row cap floor, one byte.</summary>
    public long? MinimumBytes { get; init; }

    /// <summary>The inclusive byte ceiling, 4,096 for a row cap or 16,777,216 for a chunk.</summary>
    public long? MaximumBytes { get; init; }

    /// <summary>The submitted chunk slot count for a chunk byte refusal.</summary>
    public int? ChunkSlots { get; init; }

    /// <summary>The row cap the caller submitted, in bytes, for a chunk byte refusal. It is the declared cap the
    /// chunk bound was computed from, not a limit.</summary>
    public int? RowBytes { get; init; }

    /// <summary>The row id whose type was not registered in the snapshot builder.</summary>
    public int? DefinitionId { get; init; }

    /// <summary>The derived marker names the codec writes, as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? FieldNames
    {
        get => _fieldNames;
        init => _fieldNames = value is null ? null : ImmutableArray.CreateRange(value);
    }

    /// <summary>The schema fields the codec omits, as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? MissingFields
    {
        get => _missingFields;
        init => _missingFields = value is null ? null : ImmutableArray.CreateRange(value);
    }

    /// <summary>The codec fields the schema omits, as an owned read-only snapshot.</summary>
    public IReadOnlyList<string>? ExtraFields
    {
        get => _extraFields;
        init => _extraFields = value is null ? null : ImmutableArray.CreateRange(value);
    }
}
