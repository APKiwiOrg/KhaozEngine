using System;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal;

/// <summary>Explicit diagnostic blocks. The baseline path never creates a pipeline-statistics query.</summary>
internal sealed class D3D11InvocationDiagnostics(bool requested, D3D11RecordMode mode,
    ID3D11TimestampQueries queries, object submitLock, IDeviceLiveness? liveness = null,
    D3D11DeviceLossLatch? loss = null, D3D11CreationGate? creation = null) : ID3D11InvocationDiagnostics
{
    bool _capturing;
    readonly D3D11CreationGate _creation = creation ?? D3D11CreationGate.For(null);
    readonly IDeviceLiveness _liveness = liveness ?? LiveDevice.Instance;

    internal D3D11InvocationReport Report(GpuDiagnosticStatus status, string? reason,
        ImmutableArray<GpuPipelineStatistic> statistics = default)
        => new(status, reason, mode.ToString(), statistics.IsDefault ? [] : statistics);

    public D3D11InvocationBlock CaptureInvocations(Action recordAndSubmit)
    {
        ArgumentNullException.ThrowIfNull(recordAndSubmit);
        lock (submitLock)
        {
            if (_liveness.IsDead) return new(this, null, Report(GpuDiagnosticStatus.Invalid, "The device was disposed or lost."));
            if (!requested) return new(this, null, Report(GpuDiagnosticStatus.Unsupported,
                "KE_D3D11_INVOCATION_DIAGNOSTICS=1 was not selected for this separate diagnostic device."));
            if (_capturing) throw new InvalidOperationException("Invocation capture cannot be nested.");
            object query;
            try { using (_creation.Enter()) query = queries.CreateQuery(4); } // D3D11_QUERY_PIPELINE_STATISTICS
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                loss?.Check(error.HResult, "pipeline statistics query creation");
                bool unsupported = error is NotSupportedException || error.HResult is unchecked((int)0x887A0004)
                    or unchecked((int)0x80070057) or unchecked((int)0x80004001);
                return new(this, null, Report(unsupported && !_liveness.IsDead ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Invalid,
                    $"CreateQuery(D3D11_QUERY_PIPELINE_STATISTICS) failed (0x{error.HResult:X8}): {error.Message}"));
            }
            var block = new D3D11InvocationBlock(this, query, null);
            _capturing = true;
            bool began = false, endAttempted = false;
            try
            {
                queries.Begin(query);
                began = true;
                recordAndSubmit();
                endAttempted = true;
                if (!_liveness.IsDead) queries.End(query);
                return block;
            }
            catch
            {
                // A failed workload is never returned as counter evidence. Do not mask the original failure.
                try { if (began && !endAttempted && !_liveness.IsDead) queries.End(query); }
                catch (Exception) { /* Preserve the original workload/native failure. */ }
                try { block.Dispose(); }
                catch (Exception) { /* Preserve the original workload/native failure. */ }
                throw;
            }
            finally { _capturing = false; }
        }
    }

    internal bool TryRead(D3D11InvocationBlock block, out D3D11InvocationReport? report)
    {
        lock (submitLock)
        {
            if (block.Disposed || _liveness.IsDead)
                block.Result = Report(GpuDiagnosticStatus.Invalid, "The query or device was disposed or lost.");
            if (block.Result is not null) { report = block.Result; return true; }
            Span<byte> data = stackalloc byte[88]; // Eleven UINT64 fields, in native header order.
            int result;
            try { result = queries.GetData(block.Query!, data, 1); } // D3D11_ASYNC_GETDATA_DONOTFLUSH
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                loss?.Check(error.HResult, "pipeline statistics GetData");
                block.Result = Report(GpuDiagnosticStatus.Invalid, $"GetData failed (0x{error.HResult:X8}): {error.Message}");
                report = block.Result;
                return true;
            }
            loss?.Check(result, "pipeline statistics GetData");
            if (result == 1 && !_liveness.IsDead) { report = null; return false; }
            if (result != 0 || _liveness.IsDead)
                block.Result = Report(GpuDiagnosticStatus.Invalid, $"Pipeline statistics GetData failed (0x{result:X8}).");
            else
            {
                string[] names = ["IAVertices", "IAPrimitives", "VSInvocations", "GSInvocations", "GSPrimitives",
                    "CInvocations", "CPrimitives", "PSInvocations", "HSInvocations", "DSInvocations", "CSInvocations"];
                var statistics = ImmutableArray.CreateBuilder<GpuPipelineStatistic>(11);
                for (int i = 0; i < names.Length; i++)
                {
                    ulong value = MemoryMarshal.Read<ulong>(data[(i * 8)..]);
                    statistics.Add(new(names[i], Description(i), new(new(GpuDiagnosticValueType.UInt64, UInt64: value),
                        "count", "D3D11_QUERY_DATA_PIPELINE_STATISTICS." + names[i] + " (UINT64)",
                        GpuDiagnosticStatus.Available, null, GpuDiagnosticProvenance.RuntimeCounter)));
                }
                block.Result = Report(GpuDiagnosticStatus.Available, null, statistics.MoveToImmutable());
            }
            report = block.Result;
            return true;
        }
    }

    internal void Release(D3D11InvocationBlock block)
    {
        lock (submitLock)
        {
            if (block.Disposed) return;
            block.Disposed = true;
            if (block.Query is not null && !_liveness.IsDead) queries.Destroy(block.Query);
        }
    }

    static string Description(int index) => index switch
    {
        0 => "Number of vertices read by input assembler.",
        1 => "Number of primitives read by the input assembler.",
        2 => "Number of times a vertex shader was invoked.",
        3 => "Number of times a geometry shader was invoked.",
        4 => "Number of primitives output by a geometry shader.",
        5 => "Number of primitives that were sent to the rasterizer.",
        6 => "Number of primitives that were rendered.",
        7 => "Number of times a pixel shader was invoked.",
        8 => "Number of times a hull shader was invoked.",
        9 => "Number of times a domain shader was invoked.",
        _ => "Number of times a compute shader was invoked."
    };
}
