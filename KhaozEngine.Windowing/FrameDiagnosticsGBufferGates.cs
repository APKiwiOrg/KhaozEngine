using System;
using System.Linq;

namespace KhaozEngine.Windowing;

internal sealed record FrameDiagnosticsGBufferGateDefinition(int Id, string Name, string[] SourceMethods);
internal readonly record struct FrameDiagnosticsGBufferGateBlock(int StartCallback, int EndCallbackExclusive,
    long StartTimestamp, long EndTimestamp, int GateId, string Gate)
{
    public int BlockCallbacks => FrameDiagnosticsGBufferGateSchedule.BlockCallbacks;
}
internal sealed record FrameDiagnosticsGBufferGatePair(int GateBlock, int BaselineBeforeBlock, int BaselineAfterBlock);
internal sealed record FrameDiagnosticsGBufferGateCount(int GateId, string Gate, int Blocks, int ScheduledCallbacks,
    int MatchingCallbacks, int NoFrameCallbacks, int MismatchedCallbacks, long SkippedDraws);
internal sealed record FrameDiagnosticsGBufferGateReport(FrameDiagnosticsGBufferGateDefinition[] Gates,
    int BlockCallbacks, int DiscardCallbacksPerBlock, FrameDiagnosticsGBufferGateBlock[] Schedule,
    FrameDiagnosticsGBufferGatePair[] Pairs, int[] AppliedPerCallback, long[][] SkippedDrawsPerCallback,
    FrameDiagnosticsGBufferGateCount[] Counts, long ApplyFailures);

// B G1 B G2 ... B G5 repeated three times, then baseline until the ten-second recorder stops.
// The final baseline can be longer than the nominal block, or clipped short by duration/capacity.
internal sealed class FrameDiagnosticsGBufferGateSchedule(IFrameDiagnosticsGBufferGates? gates, int capacity)
{
    internal const int BlockCallbacks = 36;
    const int GateCount = 5, LastBlock = 30, DiscardCallbacks = 6;
    static readonly FrameDiagnosticsGBufferGateDefinition[] Definitions =
    [
        new(1, "rigid", ["Scene3D.DrawOpaqueRigidGeometry"]),
        new(2, "splat", ["Scene3D.DrawSplatRuns"]),
        new(3, "tileGround", ["Scene3D.DrawTileGroundRuns"]),
        new(4, "foliage", ["Scene3D.DrawFoliagePass"]),
        new(5, "skinned", ["Scene3D.DrawGpuSkinnedMain", "Scene3D.DrawCpuSkinnedMain"]),
    ];
    readonly FrameDiagnosticsGBufferGateDefinition[] _definitions = Definitions;
    readonly int[] _applied = new int[capacity];
    readonly long[] _skipped = new long[capacity * GateCount];
    readonly FrameDiagnosticsGBufferGateBlock[] _blocks = new FrameDiagnosticsGBufferGateBlock[LastBlock + 1];
    int _blockCount;
    long _applyFailures;
    bool _stopped;

    internal void Boundary(int callback, long timestamp)
    {
        if (_stopped) return;
        if (callback == 0) gates?.ConsumeApplied(_skipped.AsSpan(0, GateCount));
        if (callback % BlockCallbacks != 0 || callback / BlockCallbacks > LastBlock) return;
        CloseBlock(callback, timestamp);
        int block = callback / BlockCallbacks;
        int gate = block % 2 == 0 ? 0 : block / 2 % GateCount + 1;
        _blocks[_blockCount++] = new(callback, callback, timestamp, timestamp, gate, Name(gate));
        Apply((GBufferGate)gate);
    }

    internal void CallbackEnded(int callback)
    {
        Span<long> skipped = _skipped.AsSpan(callback * GateCount, GateCount);
        skipped.Clear();
        _applied[callback] = gates?.ConsumeApplied(skipped) ?? (int)GBufferGate.Disabled;
    }

    internal void End(int callback, long timestamp)
    {
        CloseBlock(callback, timestamp);
        Stop();
    }

    internal void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        if (_blockCount > 0) Apply(GBufferGate.Disabled);
    }

    void CloseBlock(int callback, long timestamp)
    {
        if (_blockCount > 0)
            _blocks[_blockCount - 1] = _blocks[_blockCount - 1] with { EndCallbackExclusive = callback, EndTimestamp = timestamp };
    }

    void Apply(GBufferGate gate)
    {
        try { gates?.Apply(gate); }
        catch (Exception) { _applyFailures++; } // Recorded applied state exposes a failed transition.
    }

    string Name(int gate) => gate == 0 ? "baseline" : _definitions[gate - 1].Name;

    internal FrameDiagnosticsGBufferGateReport Report(int count)
    {
        var blocks = _blocks.Take(_blockCount).ToArray();
        var applied = _applied.Take(count).ToArray();
        var skipped = new long[count][];
        for (int callback = 0; callback < count; callback++)
            skipped[callback] = _skipped.AsSpan(callback * GateCount, GateCount).ToArray();
        var counts = new FrameDiagnosticsGBufferGateCount[GateCount + 1];
        for (int gate = 0; gate <= GateCount; gate++)
        {
            int blockCount = 0, scheduled = 0, matching = 0, noFrame = 0, mismatched = 0;
            long skippedDraws = 0;
            foreach (var block in blocks.Where(b => b.GateId == gate))
            {
                blockCount++;
                for (int callback = block.StartCallback; callback < block.EndCallbackExclusive; callback++)
                {
                    scheduled++;
                    if (applied[callback] == gate) matching++;
                    else if (applied[callback] == -1) noFrame++;
                    else mismatched++;
                }
            }
            if (gate != 0)
                for (int callback = 0; callback < count; callback++) skippedDraws += skipped[callback][gate - 1];
            counts[gate] = new(gate, Name(gate), blockCount, scheduled, matching, noFrame, mismatched, skippedDraws);
        }
        // No pair is promised unless both neighbouring baselines were observed. Analysis also rejects short blocks.
        var pairs = Enumerable.Range(0, blocks.Length).Where(b => b % 2 == 1 && b + 1 < blocks.Length)
            .Select(b => new FrameDiagnosticsGBufferGatePair(b, b - 1, b + 1)).ToArray();
        return new(_definitions, BlockCallbacks, DiscardCallbacks, blocks, pairs, applied, skipped, counts, _applyFailures);
    }
}
