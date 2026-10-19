using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;

namespace KhaozEngine.Movement;

/// <summary>Execution choices captured on the caller, never baked identity or data.</summary>
internal readonly record struct ContactNavigationReusePolicy(bool Footprints, bool InitialHolds, bool ReferenceEvaluation)
{
    internal static ContactNavigationReusePolicy Capture(NavBakeExecution? execution = null)
    {
        bool reference = SupportReferenceEvaluation.Active;
        bool stable = execution is { MaxDegreeOfParallelism: > 1 } || execution?.UseStableContactHoldReuse == true;
        return new(!reference && ContactNavigationBuildHooks.Current?.DisableFootprintReuse != true,
            !reference && stable && ContactNavigationBuildHooks.Current?.DisableInitialHoldReuse != true, reference);
    }
}

internal sealed partial class ContactNavigationProof
{
    readonly ContactNavigationReusePolicy _reuse;
    // This proof owns the fixed tuning, composition, footprint, frame and build basis for this token's lifetime.
    readonly object _profileIdentity = new();
    SourceScope? _sourceScope;

    internal bool TryEdge(Vector3 fromFeet, Vector3 toFeet)
    {
        try { return TryEdgeCore(fromFeet, toFeet, true); }
        catch (Exception failure)
        {
            if (_sourceScope is { } scope) scope.Failure = failure;
            throw;
        }
    }

    internal void AssertCurrent() => _snapshot?.AssertCurrent();

    MovementStepResult InitialHold(in FramedMovementState state, Vector3 cell, bool edgeProof, out Vector3 feet,
        out ContactNavigationHoldKey? remember)
    {
        remember = null;
        using var queries = Acquire(cell);
        EnsureSourceSession(queries);
        SourceScope? source = edgeProof && _reuse.InitialHolds && !_reuse.ReferenceEvaluation &&
            !SupportReferenceEvaluation.Active && _snapshot is not null ? _sourceScope : null;
        ContactNavigationHoldKey key = default;
        if (source is not null)
        {
            source.AssertThread();
            key = new(state, _proof.Tuning, _scope, queries.Id, _bake.Options.EdgeProbeSeconds,
                _bake.Options.MaxEdgeProbeSteps, _profileIdentity, source.Source, _snapshot!);
            if (source.Hold.TryGet(key, out var copied, out var frame, out feet, out var outcome))
            {
                // An immediate arrival has no later travel lease. Authenticate this edge before using any copy.
                _snapshot!.Authenticate(queries);
                queries.AssertCurrent();
                SupportQueryDiagnostics.Record(SupportQueryCounter.InitialHoldReuseHits);
                return new(new(copied, frame, null), outcome);
            }
        }
        MovementStepResult result = StepLeased(state, Vector2.Zero, queries, edgeProof);
        feet = Feet(result);
        if (source is not null) remember = key;
        return result;
    }

    internal IDisposable BeginSource(ContactNavigationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_sourceScope is not null) throw new InvalidOperationException("Contact navigation source groups cannot nest.");
        return _sourceScope = new(this, new(_profileIdentity, source));
    }

    internal void EnsureSourceSession(MovementQueryLease queries)
    {
        if (_sourceScope is not { } scope || !_reuse.Footprints || _reuse.ReferenceEvaluation ||
            SupportReferenceEvaluation.Active) return;
        scope.AssertThread();
        if (!scope.Attempted)
        {
            scope.Attempted = true;
            if (queries.TryOpenSupportFootprintReuse(out var session))
            {
                scope.Session = session;
                session!.ResetSource(scope.Domain);
            }
        }
        if (scope.Session is { } current) queries.AssertSupportFootprintReuseCurrent(current);
    }

    sealed class SourceScope(ContactNavigationProof owner, SupportFootprintReuseDomain domain) : IDisposable
    {
        readonly int _thread = Environment.CurrentManagedThreadId;
        internal SupportFootprintReuseDomain Domain { get; } = domain;
        internal ContactNavigationSource Source { get; } = (ContactNavigationSource)domain.SourceIdentity;
        internal ContactNavigationInitialHold Hold { get; } = new();
        internal IPhysicsSupportFootprintReuseSession? Session;
        internal bool Attempted;
        internal Exception? Failure;
        bool _disposed;

        internal void AssertThread()
        {
            if (Environment.CurrentManagedThreadId != _thread)
                throw new InvalidOperationException("A contact navigation source group belongs to its executing thread.");
        }

        public void Dispose()
        {
            AssertThread();
            if (_disposed) return;
            _disposed = true;
            Hold.Reset();
            owner._sourceScope = null;
            var session = Session;
            Session = null;
            try { session?.Dispose(); }
            catch (Exception cleanup) when (Failure is not null)
            {
                // Cleanup authenticates too. Keep the proof's original refusal when that basis already failed.
                Failure.Data["SupportFootprintReuseCleanupFailure"] = cleanup;
            }
        }
    }
}
