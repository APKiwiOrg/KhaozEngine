using System;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Refuses a foliage context option outside its admitted numeric domain.</summary>
public sealed class MapFoliageContextArgumentException : ArgumentOutOfRangeException
{
    /// <summary>Creates a refusal retaining the released parameter name and default BCL diagnostic.</summary>
    public MapFoliageContextArgumentException(MapDocumentRefusal reason, string? paramName) : base(paramName)
    {
        Reason = reason;
    }

    /// <summary>Creates a refusal with a supplied diagnostic and the original exception as its cause.</summary>
    public MapFoliageContextArgumentException(MapDocumentRefusal reason, string? message, Exception? inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The rule that refused the option.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The nested option whose value was refused.</summary>
    public string? FieldPath { get; init; }
    /// <summary>The refused integer option value.</summary>
    public long? ActualInteger { get; init; }
    /// <summary>The inclusive minimum option value.</summary>
    public long? MinimumInteger { get; init; }
}
