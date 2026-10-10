using System;

namespace KhaozEngine.MapDoc.Storage;

/// <summary>Refuses work before a bounded acquisition allowance would be exceeded.</summary>
public sealed class MapSurfaceCapacityException : Exception
{
    public MapSurfaceCapacityException() { }
}
