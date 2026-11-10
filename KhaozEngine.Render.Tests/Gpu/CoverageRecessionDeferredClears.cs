using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Metal.Internal;

namespace KhaozEngine.Tests.Gpu;

/// <summary>Records clear provenance at the actual commands and confirms consumption through Metal's
/// successful pass-begin serial. Never flushes an encoder, submits, waits or changes a native command.</summary>
internal sealed class CoverageRecessionDeferredClears(Func<int> currentScope)
{
    internal readonly record struct Origin(int Request, int Scope, int Attachment);
    internal readonly record struct Consumption(Origin Clear, int Consumer);
    readonly List<Origin> pending = [];
    Consumption[][] samples = [];
    int[] counts = [];
    bool[] available = [];
    MetalCommandList? native;
    ulong serial;
    int sample, issued, maximumRequests;
    bool discovering;

    internal void Discover() { discovering = true; Reset(-1); }
    internal void Freeze(int capacity)
    {
        samples = new Consumption[capacity][]; counts = new int[capacity]; available = new bool[capacity];
        for (int i = 0; i < capacity; i++) samples[i] = new Consumption[maximumRequests];
        discovering = false;
    }
    internal void BeginFrame(IGpuCommandList list, int id)
    {
        Reset(id); counts[id] = 0;
        native = list as MetalCommandList;
        serial = native?.Passes.EncodedClearBatchesForTests ?? 0;
        available[id] = native is not null;
    }
    void Reset(int id) { sample = id; issued = 0; native = null; pending.Clear(); }

    internal void Cleared(IGpuCommandList list, uint? colour)
    {
        Observe(list);
        if (native is null) return;
        var state = native.Passes.PendingClearForTests(colour);
        issued++;
        if (discovering) maximumRequests = Math.Max(maximumRequests, issued);
        if (!state.Pending) return;
        var origin = new Origin(issued, currentScope(), state.Attachment);
        // Another request for this attachment overwrites the deferred clear without executing it.
        for (int i = 0; i < pending.Count; i++)
            if (pending[i].Attachment == state.Attachment) { pending[i] = origin; return; }
        pending.Add(origin);
    }

    internal void Observe(IGpuCommandList list)
    {
        if (list is not MetalCommandList metal) return;
        ulong encoded = metal.Passes.EncodedClearBatchesForTests;
        // Begin discards the previous recording, including its deferred clears and diagnostic serial.
        if (!ReferenceEquals(native, metal) || encoded < serial)
        { native = metal; serial = encoded; pending.Clear(); }
        if (encoded != serial)
        {
            if (pending.Count == 0) throw new InvalidOperationException("A Metal clear consumption has no recorded request provenance.");
            foreach (var origin in pending)
                if (!discovering)
                {
                    int index = counts[sample];
                    if (index >= samples[sample].Length)
                        throw new InvalidOperationException("Metal clear consumption exceeded the cold observation reservation.");
                    samples[sample][index] = new(origin, currentScope()); counts[sample]++;
                }
            pending.Clear(); serial = encoded;
        }
        // A failed native begin followed by a target replacement can discard an unconsumed request.
        // It must never appear as GPU clear work in the replacement interval.
        if (!metal.Passes.HasPendingClears) pending.Clear();
    }

    internal ReadOnlySpan<Consumption> For(int id) => samples[id].AsSpan(0, counts[id]);
    internal bool Available(int id) => available[id];
}
