using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using KhaozEngine.Gpu;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Cold teardown ledger. A failed drain retains GPU dependants instead of releasing them.</summary>
internal sealed class CoverageRecessionCleanup
{
    readonly List<GpuDiagnosticFailure> failures = [];
    readonly List<object> retainedOwners = [];
    internal bool Active { get; set; }
    internal bool DrainFailed { get; private set; }
    internal Exception? FirstError => failures.Count == 0 ? null : failures[0].Error;
    internal void RefuseDrain() => DrainFailed = true;
    internal void Retain(object owner) => retainedOwners.Add(owner);
    internal CoverageRecessionFactoryFailure[] Snapshot() => failures
        .Select(f => CoverageRecessionFactoryFailure.Capture(f.Stage, f.Error)).ToArray();

    internal bool Release(string operation, Action release)
    {
        try { release(); return true; }
        catch (Exception error) { Capture(operation, error); return false; }
    }

    internal bool Drain(string operation, Action drain)
    {
        if (Release(operation, drain)) return true;
        DrainFailed = true;
        return false;
    }

    internal Exception ConstructionFailure(Exception error)
    {
        // The timestamp ring transports its constructor error first, followed by rollback failures.
        if (error is not GpuDiagnosticFailureException transport || transport.Failures[0].Stage != "renderer.session.construct") return error;
        for (int i = 1; i < transport.Failures.Length; i++) Capture(transport.Failures[i].Stage, transport.Failures[i].Error);
        return transport.Failures[0].Error;
    }

    void Capture(string operation, Exception error)
    {
        if (error is GpuDiagnosticFailureException transport)
        {
            foreach (var failure in transport.Failures) Capture(failure.Stage, failure.Error);
            return;
        }
        // These are the submission and scene owners' structured teardown stages, not message matching.
        DrainFailed |= error is GpuDrainDuringRecordingException
            || operation == "renderer.session.dispose.submission.drain"
            || operation.StartsWith("renderer.session.dispose.scene.drain.", StringComparison.Ordinal);
        failures.Add(new(operation, error));
    }

    internal void ThrowIfFailed()
    {
        if (failures.Count != 0) throw new GpuDiagnosticFailureException(ImmutableArray.CreateRange(failures));
    }
}
