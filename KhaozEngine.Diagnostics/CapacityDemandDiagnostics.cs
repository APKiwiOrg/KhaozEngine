using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace KhaozEngine.Diagnostics;

/// <summary>The counter's lifetime, rather than a claim that attempted demand became retained output.</summary>
public enum CapacityDemandLifetime { Inventory, Transient, Cumulative, FinalInventory }

/// <summary>One evaluated capacity gate. No authored content or mutable admission object is published.</summary>
public readonly record struct CapacityDemand(long AdmissionId, long AllowanceId, long OperationId,
    long ParentOperationId, long RootOperationId, string Operation, string Site, string LimitName,
    decimal CurrentDemand, decimal ProposedIncrement, decimal Allowance, bool Refused,
    CapacityDemandLifetime Lifetime, bool Saturated)
{
    public decimal Requested => CurrentDemand + ProposedIncrement;
}

/// <summary>Opt-in count-only observations. Observer failures cannot affect admission.</summary>
/// <remarks>Identities are process-local. Operations flow with ExecutionContext into workers. No observation storage
/// is created until Observe is called. Refused is the original evaluated predicate, including probes and eviction
/// thresholds. No later work is evaluated after an earlier gate stops it.</remarks>
public static class CapacityDemandDiagnostics
{
    static State? s_state;
    static long s_next;
    sealed class Identity { internal readonly long Id = Interlocked.Increment(ref s_next); }
    sealed class State
    {
        internal readonly object Gate = new();
        internal readonly AsyncLocal<Frame?> Current = new();
        internal readonly ConditionalWeakTable<object, Identity> Identities = new();
        internal Action<CapacityDemand>? Observer;
    }
    sealed class Frame(long id, object? admission, string operation, Frame? parent)
    {
        internal readonly long Id = id, RootId = parent?.RootId ?? id;
        internal readonly object? Admission = admission;
        internal readonly string Operation = operation;
        internal readonly Frame? Parent = parent;
    }

    public static bool IsObserved
    {
        get { var state = Volatile.Read(ref s_state); return state is not null && Volatile.Read(ref state.Observer) is not null; }
    }

    /// <summary>Subscribes synchronously on the evaluating thread. A shared collector must synchronize its storage.</summary>
    public static IDisposable Observe(Action<CapacityDemand> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var state = Volatile.Read(ref s_state);
        if (state is null)
        {
            var created = new State();
            state = Interlocked.CompareExchange(ref s_state, created, null) ?? created;
        }
        lock (state.Gate) Volatile.Write(ref state.Observer, state.Observer + observer);
        return new Subscription(state, observer);
    }

    sealed class Subscription(State state, Action<CapacityDemand> observer) : IDisposable
    {
        bool _disposed;
        public void Dispose()
        {
            lock (state.Gate)
            {
                if (_disposed) return;
                _disposed = true;
                Volatile.Write(ref state.Observer, state.Observer - observer);
            }
        }
    }

    /// <summary>Names one operation without altering its admission lifetime. Dispose in nesting order.</summary>
    public static OperationScope Enter(string operation) => Enter(null, operation);
    public static OperationScope Enter(object? admission, string operation)
    {
        var state = Volatile.Read(ref s_state);
        if (state is null || Volatile.Read(ref state.Observer) is null) return default;
        try
        {
            var previous = state.Current.Value;
            state.Current.Value = new(Interlocked.Increment(ref s_next), admission, operation, previous);
            return new(state, previous);
        }
        catch { return default; }
    }

    /// <summary>Publishes the original gate result and returns it unchanged. No capacity decision is computed here.</summary>
    public static bool Check(object? admission, string site, string limitName, decimal currentDemand,
        decimal proposedIncrement, decimal allowance, bool refused,
        CapacityDemandLifetime lifetime = CapacityDemandLifetime.Cumulative, object? allowanceIdentity = null, bool saturated = false)
    {
        var state = Volatile.Read(ref s_state);
        if (state is null || Volatile.Read(ref state.Observer) is not { } observer) return refused;
        try
        {
            var frame = state.Current.Value;
            object identity = admission ?? frame?.Admission ?? (object?)frame ?? state;
            long id = state.Identities.GetValue(identity, static _ => new()).Id;
            long allowanceId = allowanceIdentity is null ? id : state.Identities.GetValue(allowanceIdentity, static _ => new()).Id;
            var demand = new CapacityDemand(id, allowanceId, frame?.Id ?? id, frame?.Parent?.Id ?? 0, frame?.RootId ?? id,
                frame?.Operation ?? site, site, limitName, currentDemand, proposedIncrement, allowance, refused, lifetime, saturated);
            foreach (Action<CapacityDemand> callback in observer.GetInvocationList())
                try { callback(demand); } catch { /* One failing collector cannot affect admission or another collector. */ }
        }
        catch { /* Diagnostics cannot change a reservation, counter, allowance or exception. */ }
        return refused;
    }

    public readonly struct OperationScope : IDisposable
    {
        readonly object? _state, _previous;
        internal OperationScope(object state, object? previous) { _state = state; _previous = previous; }
        public void Dispose()
        { try { if (_state is State state) state.Current.Value = (Frame?)_previous; } catch { } }
    }
}
