using System;
using System.Collections.Generic;
using KhaozEngine.Ecs;

namespace KhaozEngine.Sharding;

public enum CellAdmissionOutcome { Accepted, Unresolved, Refused }
public enum CellImportPurpose { Restore, Transfer, Relocate }

/// <summary>Classifies frame-adapted staged entities before any live publication. Implementations
/// may update staged pure state. The returned read remains held through publication and ownership changes.</summary>
public interface ICellImportAdmission
{
    CellAdmissionRead Acquire(World staged, IReadOnlyDictionary<long, Entity> entities, CellImportPurpose purpose);
}

/// <summary>A thread-affine import verdict and optional read lease. Unresolved/refused environment
/// data is distinct from a corrupt snapshot and never authorizes partial publication.</summary>
public sealed class CellAdmissionRead : IDisposable
{
    readonly IDisposable? lease;
    readonly int thread = Environment.CurrentManagedThreadId;
    bool disposed;
    public CellAdmissionOutcome Outcome { get; }
    public string? Detail { get; }
    public CellAdmissionRead(CellAdmissionOutcome outcome, IDisposable? lease = null, string? detail = null)
    {
        if (outcome is < CellAdmissionOutcome.Accepted or > CellAdmissionOutcome.Refused)
            throw new ArgumentOutOfRangeException(nameof(outcome));
        Outcome = outcome;
        Detail = detail;
        this.lease = lease;
    }
    internal void AssertUsable()
    {
        if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("A cell admission read belongs to its acquiring thread.");
        ObjectDisposedException.ThrowIf(disposed, this);
    }
    public void Dispose()
    {
        if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("A cell admission read belongs to its acquiring thread.");
        if (disposed) return;
        disposed = true;
        lease?.Dispose();
    }
}
