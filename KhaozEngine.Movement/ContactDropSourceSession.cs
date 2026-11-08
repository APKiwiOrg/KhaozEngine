using System;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Full-tuning, attempt-local support-query reuse. A new mode or source gets a new domain.
/// No movement result, initial Hold or trajectory prefix is cached here.</summary>
internal sealed class ContactDropSourceSession(object profile, bool enabled) : IDisposable
{
    readonly object _source = new();
    IPhysicsSupportFootprintReuseSession? _session;
    bool _attempted;
    internal Exception? Failure { get; set; }

    internal void Bind(MovementQueryLease queries)
    {
        if (!enabled || SupportReferenceEvaluation.Active) return;
        if (!_attempted)
        {
            _attempted = true;
            if (queries.TryOpenSupportFootprintReuse(out _session)) _session!.ResetSource(new(profile, _source));
        }
        if (_session is not null) queries.AssertSupportFootprintReuseCurrent(_session);
    }

    public void Dispose()
    {
        try { _session?.Dispose(); }
        catch (Exception cleanup) when (Failure is not null)
        { Failure.Data["DropSupportFootprintReuseCleanupFailure"] = cleanup; }
    }
}
