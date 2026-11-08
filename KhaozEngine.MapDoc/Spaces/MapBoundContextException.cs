using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>A read outside the bound geometry demand set retained by a prepared context.</summary>
public sealed class MapBoundContextException : InvalidOperationException
{
    /// <summary>Creates a refusal naming the unprepared demand.</summary>
    public MapBoundContextException(MapDocumentRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>Creates a refusal retaining its original cause.</summary>
    public MapBoundContextException(MapDocumentRefusal reason, string message, Exception inner) : base(message, inner) => Reason = reason;

    /// <summary>The rule that refused this context read.</summary>
    public MapDocumentRefusal Reason { get; }
    /// <summary>The patch requested by the read, when available.</summary>
    public MapPatchKey? Patch { get; init; }
    /// <summary>The opening owner requested by an opening-cell read.</summary>
    public string? RecordId { get; init; }
    /// <summary>The requested cell within its 64 by 64 patch slot, when applicable.</summary>
    public int? SlotCell { get; init; }
    /// <summary>The exact compiled face requested by a face read.</summary>
    public MapFaceKey? Face { get; init; }
    /// <summary>The context operation whose demand was not prepared.</summary>
    public string? Operation { get; init; }
}
