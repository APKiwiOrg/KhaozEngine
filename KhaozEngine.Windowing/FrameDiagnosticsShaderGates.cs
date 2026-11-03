using System;
using System.Linq;

namespace KhaozEngine.Windowing;

/// <summary>Private recorder seam. Apply runs on the frame thread at a callback's opening boundary, before prepare or
/// update, so one callback sees one state. Neither member may allocate or do I/O.</summary>
public interface IFrameDiagnosticsShaderGates
{
    /// <summary>The gate mask for the callback about to run. Bit i is <see cref="FrameDiagnosticsShaderGateSchedule.GateNames"/>[i].</summary>
    void Apply(int gates);
    /// <summary>The mask the frames since the last call actually uploaded, or a negative value (see the report).</summary>
    int ConsumeApplied();
    /// <summary>Exact internal/display size equality with TAA active, 0 or 1, or the same negative sentinels.</summary>
    int ConsumeExactSizeCondition() => -3;
}

internal readonly record struct FrameDiagnosticsShaderGateBlock(int StartCallback, int EndCallbackExclusive,
    long StartTimestamp, long EndTimestamp, string State, int Gates);

internal sealed record FrameDiagnosticsShaderGateCount(string State, int Gates, int Blocks, int ScheduledCallbacks,
    int MatchingCallbacks, int NoFrameCallbacks, int MismatchedCallbacks);

/// <summary>AppliedPerCallback holds, per sample, the uploaded mask, -1 for no rendered frame, -2 for renders that
/// disagreed, or -3 with nothing registered.</summary>
internal sealed record FrameDiagnosticsShaderGateReport(string[] Names, int BlockCallbacks,
    FrameDiagnosticsShaderGateBlock[] Schedule, int[] AppliedPerCallback, FrameDiagnosticsShaderGateCount[] Counts, long ApplyFailures)
{
    public int[] ExactSizePerCallback { get; init; } = [];
    internal static readonly FrameDiagnosticsShaderGateReport Empty = new([], 0, [], [], [], 0);
}

/// <summary>Rotates the candidate and its three cuts, or the legacy baseline and gates, in blocks of 72 callbacks. Gates are off outside
/// the sampling window, because only an unstopped schedule ever applies a nonzero mask and every way out stops it.</summary>
internal sealed class FrameDiagnosticsShaderGateSchedule(IFrameDiagnosticsShaderGates? gates, int capacity, bool rotate = true, bool candidate = false)
{
    internal const int BlockCallbacks = 72;
    internal const int NotRegistered = -3;
    internal static readonly string[] GateNames = ["pointShadowZeroNdl", "underwaterMarchZero", "rigidWorldReuse", "lanczosExactSizeReuse", "zeroSpecular", "tileSlotReuse"];
    static readonly (string Name, int Gates)[] States =
        [("baseline", 0), (GateNames[0], 1), (GateNames[1], 2), (GateNames[2], 4)];

    readonly (string Name, int Gates)[] _states = candidate
        ? [("candidate", 1), (GateNames[3], 9), (GateNames[4], 17), (GateNames[5], 33)] : States;
    readonly int[] _exactSize = new int[capacity];
    readonly int[] _applied = new int[capacity];
    readonly FrameDiagnosticsShaderGateBlock[] _blocks = new FrameDiagnosticsShaderGateBlock[capacity / BlockCallbacks + 1];
    int _blockCount;
    long _applyFailures;
    bool _nonzero, _stopped;

    internal void Boundary(int callback, long timestamp)
    {
        if (_stopped) return;
        if (callback == 0) { gates?.ConsumeApplied(); gates?.ConsumeExactSizeCondition(); } // A value from before the window describes no sample.
        if (callback % BlockCallbacks != 0) return;
        CloseBlock(callback, timestamp);
        var state = _states[rotate ? callback / BlockCallbacks % _states.Length : 0];
        _blocks[_blockCount++] = new(callback, callback, timestamp, timestamp, state.Name, state.Gates);
        Apply(state.Gates);
    }

    internal void CallbackEnded(int callback)
    {
        _applied[callback] = gates?.ConsumeApplied() ?? NotRegistered;
        _exactSize[callback] = gates?.ConsumeExactSizeCondition() ?? NotRegistered;
    }

    internal void End(int callback, long timestamp)
    {
        CloseBlock(callback, timestamp);
        Stop();
    }

    /// <summary>Turns everything off for good. Safe on every path out of sampling, and before sampling ever started.</summary>
    internal void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        if (_nonzero) Apply(0);
    }

    void CloseBlock(int callback, long timestamp)
    {
        if (_blockCount > 0)
            _blocks[_blockCount - 1] = _blocks[_blockCount - 1] with { EndCallbackExclusive = callback, EndTimestamp = timestamp };
    }

    void Apply(int mask)
    {
        _nonzero = mask != 0;
        try { gates?.Apply(mask); }
        catch (Exception) { _applyFailures++; } // Visible as a mismatch in the per-callback record.
    }

    internal FrameDiagnosticsShaderGateReport Report(int count)
    {
        var blocks = _blocks.Take(_blockCount).ToArray();
        var applied = _applied.Take(count).ToArray();
        var counts = _states.Select(state =>
        {
            int blockCount = 0, scheduled = 0, matching = 0, noFrame = 0, mismatched = 0;
            foreach (var block in blocks.Where(b => b.Gates == state.Gates))
            {
                blockCount++;
                for (int i = block.StartCallback; i < block.EndCallbackExclusive && i < count; i++)
                {
                    scheduled++;
                    if (applied[i] == state.Gates) matching++;
                    else if (applied[i] == -1) noFrame++;
                    else mismatched++;
                }
            }
            return new FrameDiagnosticsShaderGateCount(state.Name, state.Gates, blockCount, scheduled, matching, noFrame, mismatched);
        }).ToArray();
        return new(GateNames, BlockCallbacks, blocks, applied, counts, _applyFailures) { ExactSizePerCallback = _exactSize.Take(count).ToArray() };
    }
}
