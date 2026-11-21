#if CONTACT_SHORE_ATTRIBUTION
using System;
using System.Diagnostics;

namespace KhaozEngine.Physics;

internal enum ShorePhase
{
    Step, Acquire, Dispose, Authenticate,
    WaterPlacement, WaterStep, WaterBoundary, WaterPoint, WaterTraceLease, WaterBufferReturn, WaterSampler, WaterKernel,
    FindRead, Find, Footprint, Analytic, Binding,
    ShellPenetration, ShellSweep, SolidContacts, CertifiedSweep
}

internal enum ShoreEvent
{
    AuthMovement, AuthPhysics, AuthPin,
    FlatTrace, FlatResultHit, PrismResultHit, FlatSolve, PrismSolve, SharedProjectionCopies
}

internal enum ShoreProbeMode { Off, Counts, Timings }

// Single-threaded nested wall clocks. Attribute each timestamp interval to the currently active scope.
// Counter mode takes no timestamps. Timing mode leaves the optional engine work counters disabled.
internal static class ShoreStepProbe
{
    internal static readonly string[] Phases = Enum.GetNames<ShorePhase>();
    internal static readonly string[] Events = Enum.GetNames<ShoreEvent>();

    [ThreadStatic] static State? _state;
    [ThreadStatic] static ShoreProbeMode _mode;

    internal static void Start(ShoreProbeMode mode)
    {
        if (_mode != ShoreProbeMode.Off || mode == ShoreProbeMode.Off)
            throw new InvalidOperationException("Expected a new shore attribution sample.");
        var state = _state ??= new();
        Array.Clear(state.Calls);
        Array.Clear(state.EventCounts);
        Array.Clear(state.Inclusive);
        Array.Clear(state.Exclusive);
        _mode = mode;
    }

    [ThreadStatic] internal static bool TraceLabelsEnabled;
    [ThreadStatic] internal static string? TraceLabel;

    internal static TraceLabelScope LabelTrace(string label)
    {
        if (!TraceLabelsEnabled) return default;
        var scope = new TraceLabelScope(TraceLabel, true);
        TraceLabel = label;
        return scope;
    }

    internal readonly struct TraceLabelScope(string? previous, bool active) : IDisposable
    {
        public void Dispose()
        {
            if (active) TraceLabel = previous;
        }
    }

    internal static void Stop() => _mode = ShoreProbeMode.Off;

    internal static void Count(ShoreEvent kind, int amount = 1)
    {
        if (_mode == ShoreProbeMode.Counts) _state!.EventCounts[(int)kind] += amount;
    }

    internal static Scope Authenticate(ShoreEvent layer)
    {
        Count(layer);
        var state = _state;
        // Consecutive authentication layers time as one envelope. The count pass still counts all three.
        if (_mode == ShoreProbeMode.Timings && state!.Depth > 0 &&
            state.Stack[state.Depth - 1] == ShorePhase.Authenticate)
            return default;
        return Begin(ShorePhase.Authenticate);
    }

    internal static Scope Begin(ShorePhase phase)
    {
        if (_mode == ShoreProbeMode.Off) return default;
        var state = _state!;
        state.Calls[(int)phase]++;
        if (_mode == ShoreProbeMode.Counts) return default;

        long now = Stopwatch.GetTimestamp();
        if (state.Depth > 0) state.Exclusive[(int)state.Stack[state.Depth - 1]] += now - state.Last;
        state.Stack[state.Depth++] = phase;
        state.Last = now;
        return new Scope(state, phase, now);
    }

    internal static Reading Read()
    {
        var state = _state!;
        if (state.Depth != 0) throw new InvalidOperationException("A shore attribution scope is still open.");
        return new((int[])state.Calls.Clone(), (int[])state.EventCounts.Clone(),
            (long[])state.Inclusive.Clone(), (long[])state.Exclusive.Clone());
    }

    internal sealed record Reading(int[] Calls, int[] Events, long[] InclusiveTicks, long[] ExclusiveTicks);

    internal sealed class State
    {
        internal readonly int[] Calls = new int[Phases.Length];
        internal readonly int[] EventCounts = new int[Events.Length];
        internal readonly long[] Inclusive = new long[Phases.Length];
        internal readonly long[] Exclusive = new long[Phases.Length];
        internal readonly ShorePhase[] Stack = new ShorePhase[64];
        internal long Last;
        internal int Depth;
    }

    internal readonly struct Scope(State state, ShorePhase phase, long start) : IDisposable
    {
        public void Dispose()
        {
            if (state is null) return;
            long now = Stopwatch.GetTimestamp();
            state.Exclusive[(int)phase] += now - state.Last;
            state.Inclusive[(int)phase] += now - start;
            state.Depth--;
            state.Last = now;
        }
    }
}
#endif
