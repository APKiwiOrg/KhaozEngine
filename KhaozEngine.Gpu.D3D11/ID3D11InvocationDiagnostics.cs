using System;
using System.Collections.Immutable;
using KhaozEngine.Gpu.D3D11.Internal;

namespace KhaozEngine.Gpu.D3D11;

/// <summary>Optional D3D11-only invocation collection. Requires a separately enabled diagnostic device,
/// outside timing blocks. The callback must synchronously record AND submit its complete workload.</summary>
public interface ID3D11InvocationDiagnostics
{
    /// <summary>Brackets native work without changing the device's effective recording driver. Does not flush,
    /// wait, submit extra work or retry. An unsupported device returns an unsupported block without calling the callback.</summary>
    D3D11InvocationBlock CaptureInvocations(Action recordAndSubmit);
}

/// <summary>Terminal invocation evidence. Pending data is represented by TryRead returning false.</summary>
/// <param name="Status">Whether native results are available, unsupported or invalid.</param>
/// <param name="Reason">Unsupported or invalid cause, otherwise null.</param>
/// <param name="RecordingMode">Effective engine driver, Immediate or Deferred (engine stream replay on the immediate native context).</param>
/// <param name="Statistics">Unmodified D3D11_QUERY_DATA_PIPELINE_STATISTICS names and UINT64 counters.</param>
public sealed record D3D11InvocationReport(GpuDiagnosticStatus Status, string? Reason, string RecordingMode,
    ImmutableArray<GpuPipelineStatistic> Statistics);

/// <summary>One owned asynchronous diagnostic query. After CaptureInvocations returns, arrange normal
/// progress covering the query End. Fences submitted inside the callback precede End and do not establish
/// query completion. Keep pending results pending until post-return progress occurs. Caller-owned completion
/// waits belong outside the callback and timing. A pending read never flushes. Dispose releases the query.
/// This is independent of timestamp/disjoint pools.</summary>
public sealed class D3D11InvocationBlock : IDisposable
{
    readonly D3D11InvocationDiagnostics _owner;
    internal object? Query { get; }
    internal D3D11InvocationReport? Result { get; set; }
    internal bool Disposed { get; set; }

    internal D3D11InvocationBlock(D3D11InvocationDiagnostics owner, object? query, D3D11InvocationReport? result)
        => (_owner, Query, Result) = (owner, query, result);

    /// <summary>Returns false with a null report for S_FALSE. Terminal reads are cached. Failures carry no counters.</summary>
    public bool TryRead(out D3D11InvocationReport? report) => _owner.TryRead(this, out report);

    /// <inheritdoc/>
    public void Dispose()
    {
        _owner.Release(this);
    }
}
