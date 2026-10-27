using System;
using System.Collections.Generic;
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

/// <summary>One originating profile field behind a derived allowance. A gate demand <c>d</c>, in the derived
/// allowance's units, is this field's <c>Consumed + Scale * d</c> against its original <c>Allowance</c>.</summary>
public readonly record struct CapacityDemandOrigin(string LimitName, decimal Consumed, decimal Allowance,
    object? Identity = null, decimal Scale = 1);

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
        internal readonly ConditionalWeakTable<object, Derivation> Derived = new();
        internal Action<CapacityDemand>? Observer;
    }
    sealed class Derivation(string limitName, CapacityDemandOrigin[] origins, Derivation? next)
    {
        internal CapacityDemandOrigin[]? Find(string name)
        {
            for (var entry = this; entry is not null; entry = entry.Next)
                if (string.Equals(entry.LimitName, name, StringComparison.Ordinal)) return entry.Origins;
            return null;
        }
        internal readonly string LimitName = limitName;
        internal readonly CapacityDemandOrigin[] Origins = origins;
        internal readonly Derivation? Next = next;
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
    /// <remarks>When <paramref name="allowanceSource"/> was derived for <paramref name="limitName"/>, one event per
    /// originating profile field replaces the derived event. Each origin event compares its own requested total.</remarks>
    public static bool Check(object? admission, string site, string limitName, decimal currentDemand,
        decimal proposedIncrement, decimal allowance, bool refused,
        CapacityDemandLifetime lifetime = CapacityDemandLifetime.Cumulative, object? allowanceIdentity = null, bool saturated = false,
        object? allowanceSource = null)
    {
        var state = Volatile.Read(ref s_state);
        if (state is null || Volatile.Read(ref state.Observer) is not { } observer) return refused;
        Publish(state, observer, admission, site, limitName, currentDemand, proposedIncrement, allowance, refused,
            lifetime, allowanceIdentity, saturated, allowanceSource);
        return refused;
    }

    /// <summary>Names the originating profile fields of an allowance derived as a remainder or a minimum. Later gates
    /// that pass <paramref name="derived"/> as their allowance source publish these origins. Unobserved calls do nothing.</summary>
    public static void Derive(object derived, string limitName, IEnumerable<CapacityDemandOrigin> origins)
    {
        var state = Volatile.Read(ref s_state);
        if (state is null || Volatile.Read(ref state.Observer) is null || derived is null || limitName is null || origins is null) return;
        try
        {
            var unique = new List<CapacityDemandOrigin>();
            foreach (var origin in origins)
                if (origin.LimitName is not null && !unique.Exists(o => o.LimitName == origin.LimitName && o.Consumed == origin.Consumed &&
                    o.Allowance == origin.Allowance && o.Scale == origin.Scale)) unique.Add(origin);
            lock (state.Gate)
            {
                state.Derived.TryGetValue(derived, out var previous);
                state.Derived.AddOrUpdate(derived, new(limitName, unique.ToArray(), previous));
            }
        }
        catch { /* Provenance cannot affect a capacity decision. */ }
    }

    /// <summary>Gives a defensive copy of a derived limit record the same origins. Unobserved calls do nothing.</summary>
    public static void Inherit(object copy, object source)
    {
        var state = Volatile.Read(ref s_state);
        if (state is null || copy is null || source is null || ReferenceEquals(copy, source) ||
            !state.Derived.TryGetValue(source, out var derivation)) return;
        try { lock (state.Gate) state.Derived.AddOrUpdate(copy, derivation); }
        catch { /* Provenance cannot affect a capacity decision. */ }
    }

    /// <summary>The origins of an allowance after <paramref name="consumed"/> of it is spent and each remaining unit is
    /// worth <paramref name="scale"/> units of the new allowance. An underived source is its own single origin.</summary>
    public static CapacityDemandOrigin[] Origins(object? source, string limitName, decimal allowance, decimal consumed = 0, decimal scale = 1)
    {
        var state = Volatile.Read(ref s_state);
        if (state is not null && source is not null && state.Derived.TryGetValue(source, out var derivation) &&
            derivation.Find(limitName) is { } origins)
        {
            var result = new CapacityDemandOrigin[origins.Length];
            for (int i = 0; i < origins.Length; i++)
                result[i] = origins[i] with { Consumed = origins[i].Consumed + origins[i].Scale * consumed, Scale = origins[i].Scale * scale };
            return result;
        }
        return [new(limitName, consumed, allowance, source, scale)];
    }

    // Keep the lambda cache and identity machinery out of the first unobserved Check's JIT path.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Publish(State state, Action<CapacityDemand> observer, object? admission, string site, string limitName,
        decimal currentDemand, decimal proposedIncrement, decimal allowance, bool refused,
        CapacityDemandLifetime lifetime, object? allowanceIdentity, bool saturated, object? allowanceSource)
    {
        try
        {
            var frame = state.Current.Value;
            object identity = admission ?? frame?.Admission ?? (object?)frame ?? state;
            long id = state.Identities.GetValue(identity, static _ => new()).Id;
            long allowanceId = allowanceIdentity is null ? id : state.Identities.GetValue(allowanceIdentity, static _ => new()).Id;
            if (allowanceSource is not null && state.Derived.TryGetValue(allowanceSource, out var derivation) &&
                derivation.Find(limitName) is { } origins)
            {
                foreach (var origin in origins)
                {
                    decimal current, increment;
                    bool wide = saturated, over;
                    try
                    {
                        current = origin.Consumed + origin.Scale * currentDemand;
                        increment = origin.Scale * proposedIncrement;
                        over = current + increment > origin.Allowance;
                    }
                    catch (OverflowException) { current = decimal.MaxValue; increment = 0; wide = true; over = true; }
                    long originId = origin.Identity is null ? allowanceId : state.Identities.GetValue(origin.Identity, static _ => new()).Id;
                    Emit(observer, new(id, originId, frame?.Id ?? id, frame?.Parent?.Id ?? 0, frame?.RootId ?? id,
                        frame?.Operation ?? site, site, origin.LimitName, current, increment, origin.Allowance, over, lifetime, wide));
                }
                return;
            }
            Emit(observer, new(id, allowanceId, frame?.Id ?? id, frame?.Parent?.Id ?? 0, frame?.RootId ?? id,
                frame?.Operation ?? site, site, limitName, currentDemand, proposedIncrement, allowance, refused, lifetime, saturated));
        }
        catch { /* Diagnostics cannot change a reservation, counter, allowance or exception. */ }
    }

    static void Emit(Action<CapacityDemand> observer, CapacityDemand demand)
    {
        foreach (Action<CapacityDemand> callback in observer.GetInvocationList())
            try { callback(demand); } catch { /* One failing collector cannot affect admission or another collector. */ }
    }

    public readonly struct OperationScope : IDisposable
    {
        readonly object? _state, _previous;
        internal OperationScope(object state, object? previous) { _state = state; _previous = previous; }
        public void Dispose()
        { try { if (_state is State state) state.Current.Value = (Frame?)_previous; } catch { } }
    }
}
