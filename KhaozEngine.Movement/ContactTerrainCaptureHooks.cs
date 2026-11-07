using System;

namespace KhaozEngine.Movement;

internal enum ContactTerrainCaptureStage { RawColumns, Geometry, Index, Columns, Adjacency }

/// <summary>Calling-thread bounded capture observations. No observer means no retained diagnostic work.</summary>
internal sealed class ContactTerrainCaptureHooks
{
    [ThreadStatic] internal static ContactTerrainCaptureHooks? Current;
    internal Action<ContactTerrainCaptureStage, int, int>? Progress { get; init; }
    internal Action<long>? IndexStorage { get; init; }
    internal static Scope Install(ContactTerrainCaptureHooks hooks)
    { var prior = Current; Current = hooks; return new(prior); }
    internal readonly struct Scope(ContactTerrainCaptureHooks? prior) : IDisposable
    { public void Dispose() => Current = prior; }
}
