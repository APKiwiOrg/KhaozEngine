using System;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Refuses work before a bounded acquisition allowance would be exceeded.</summary>
public sealed class MapSurfaceCapacityException : Exception
{
    public MapSurfaceCapacityException() { }
    internal MapSurfaceCapacityException(string? diagnostic) : base(diagnostic) => Diagnostic = diagnostic;
    /// <summary>A specific refusal reason, such as a missing composition observation capability.</summary>
    internal string? Diagnostic { get; }
}
