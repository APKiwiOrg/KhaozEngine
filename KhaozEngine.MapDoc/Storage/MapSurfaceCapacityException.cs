using System;
using KhaozEngine.MapDoc.Scene;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Refuses work before a bounded acquisition allowance would be exceeded.</summary>
public sealed class MapSurfaceCapacityException : Exception
{
    public MapSurfaceCapacityException() { }
    public MapSurfaceCapacityException(string name, decimal requested, long allowance) : this(null, new(name, requested, allowance)) { }
    internal MapSurfaceCapacityException(string? diagnostic, MapSceneCapacity? capacity = null) : base(capacity?.Message ?? diagnostic)
    { Diagnostic = capacity?.Message ?? diagnostic; Capacity = capacity; }
    public MapSceneCapacity? Capacity { get; }
    /// <summary>A specific refusal reason, such as a missing composition observation capability.</summary>
    internal string? Diagnostic { get; }
}
