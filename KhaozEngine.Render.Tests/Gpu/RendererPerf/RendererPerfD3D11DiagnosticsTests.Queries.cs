using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using KhaozEngine.Gpu.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfD3D11DiagnosticsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Invocations_bracket_actual_driver_submission_and_preserve_pending(bool immediate)
    {
        var mode = immediate ? D3D11RecordMode.Immediate : D3D11RecordMode.Deferred;
        var api = new QueryApi();
        var source = new D3D11InvocationDiagnostics(true, mode, api, new object());
        using var block = source.CaptureInvocations(() =>
        {
            api.Events.Add("record");
            // Deferred engine streams emit their work only on submit. Immediate emits while recording.
            var log = new D3D11EmitterCallLog();
            var emitter = new D3D11CountingEmitter(log);
            using var list = D3D11CommandDrivers.Create(mode, emitter);
            list.Begin(); list.Draw(3); list.End();
            Assert.Equal(immediate ? 1 : 0, log.Count(D3D11OpCode.Draw));
            D3D11CommandDrivers.Submit(new object(), list, ref emitter);
            Assert.Equal(1, log.Count(D3D11OpCode.Draw));
            api.Events.Add("submitted");
        });
        Assert.Equal(new[] { "create:4", "begin", "record", "submitted", "end" }, api.Events);
        Assert.False(block.TryRead(out var pending));
        Assert.Null(pending);
        Assert.Equal(1, api.Reads);
        api.Result = 0;
        Assert.True(block.TryRead(out var report));
        Assert.Equal(GpuDiagnosticStatus.Available, report!.Status);
        Assert.Equal(mode.ToString(), report.RecordingMode);
        Assert.Equal(11, report.Statistics.Length);
        Assert.Equal(9007199254740993ul, report.Statistics.Single(x => x.Name == "CSInvocations").Metric.Value!.UInt64);
        Assert.Equal(7ul, report.Statistics.Single(x => x.Name == "PSInvocations").Metric.Value!.UInt64);
        Assert.True(block.TryRead(out var cached));
        Assert.Same(report, cached);
        Assert.Equal(2, api.Reads);
        block.Dispose();
        block.Dispose();
        Assert.Equal(1, api.Events.Count(x => x == "destroy"));
        Assert.True(block.TryRead(out var disposed));
        Assert.Equal(GpuDiagnosticStatus.Invalid, disposed!.Status);
    }

    [Fact]
    public void Failed_optional_query_keeps_ordinary_timestamps_supported()
    {
        var api = new QueryApi { RejectStatistics = true };
        var source = new D3D11InvocationDiagnostics(true, D3D11RecordMode.Deferred, api, new object());
        bool executed = false;
        using var block = source.CaptureInvocations(() => executed = true);
        Assert.True(block.TryRead(out var report));
        Assert.Equal(GpuDiagnosticStatus.Unsupported, report!.Status);
        Assert.Contains("statistics unavailable", report.Reason);
        Assert.Empty(report.Statistics);
        Assert.False(executed);
        Assert.True(D3D11TimestampApi.Probe(new D3D11TimestampApi(api)));
        Assert.Equal(new[] { "create:4", "create:2", "create:3", "destroy", "destroy" }, api.Events);
    }

    [Fact]
    public void Query_failure_is_terminal_and_failed_callback_releases_once()
    {
        var api = new QueryApi { Result = unchecked((int)0x80004005) };
        var source = new D3D11InvocationDiagnostics(true, D3D11RecordMode.Immediate, api, new object());
        using var block = source.CaptureInvocations(() => { });
        Assert.True(block.TryRead(out var report));
        Assert.Equal(GpuDiagnosticStatus.Invalid, report!.Status);
        Assert.Contains("0x80004005", report.Reason);
        Assert.Empty(report.Statistics);
        Assert.True(block.TryRead(out _));
        Assert.Equal(1, api.Reads);
        var failedApi = new QueryApi();
        var failedSource = new D3D11InvocationDiagnostics(true, D3D11RecordMode.Deferred, failedApi, new object());
        Assert.Throws<InvalidOperationException>(() => failedSource.CaptureInvocations(() => throw new InvalidOperationException("workload")));
        Assert.Equal(new[] { "create:4", "begin", "end", "destroy" }, failedApi.Events);
    }

    [Fact]
    public void Callback_failure_preserves_original_when_native_cleanup_fails()
    {
        var api = new QueryApi { EndFailure = true, DestroyFailure = true };
        var source = new D3D11InvocationDiagnostics(true, D3D11RecordMode.Deferred, api, new object());
        var original = new InvalidOperationException("original workload failure");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => source.CaptureInvocations(() => throw original)));
        Assert.Equal(new[] { "create:4", "begin", "end", "destroy" }, api.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Query_creation_obeys_the_shared_driver_gate(bool concurrentCreates)
    {
        var gate = new D3D11CreationGate(concurrentCreates);
        var api = new QueryApi
        {
            Result = 0,
            CreateObservation = () => Assert.Equal(!concurrentCreates, gate.IsEnteredByCurrentThread),
            BeginObservation = () => Assert.False(gate.IsEnteredByCurrentThread)
        };
        var source = new D3D11InvocationDiagnostics(true, D3D11RecordMode.Deferred, api, new object(), creation: gate);
        bool executed = false;
        using var block = source.CaptureInvocations(() => executed = true);
        Assert.True(executed);
        Assert.True(block.TryRead(out var report));
        Assert.Equal(GpuDiagnosticStatus.Available, report!.Status);
    }

    [Fact]
    public void Device_death_invalidates_without_reading_or_releasing_native_queries()
    {
        var api = new QueryApi();
        var live = new DeviceLiveness();
        var source = new D3D11InvocationDiagnostics(true, D3D11RecordMode.Deferred, api, new object(), live);
        using var block = source.CaptureInvocations(() => { });
        live.MarkDead();
        Assert.True(block.TryRead(out var report));
        Assert.Equal(GpuDiagnosticStatus.Invalid, report!.Status);
        Assert.Empty(report.Statistics);
        Assert.Equal(0, api.Reads);
        block.Dispose();
        Assert.DoesNotContain("destroy", api.Events);
        using var refused = source.CaptureInvocations(() => throw new InvalidOperationException("must not run"));
        Assert.True(refused.TryRead(out var dead));
        Assert.Equal(GpuDiagnosticStatus.Invalid, dead!.Status);
    }

    internal sealed class QueryApi : ID3D11TimestampQueries
    {
        internal readonly List<string> Events = [];
        internal int Reads, Result = 1;
        internal Exception? CreationFailure;
        internal Action? CreateObservation, BeginObservation;
        internal bool RejectStatistics, EndFailure, DestroyFailure;
        public object CreateQuery(int type)
        {
            Events.Add("create:" + type);
            CreateObservation?.Invoke();
            if (CreationFailure is not null) throw CreationFailure;
            if (RejectStatistics && type == 4) throw new NotSupportedException("statistics unavailable");
            return new object();
        }
        public void Begin(object query) { Events.Add("begin"); BeginObservation?.Invoke(); }
        public void End(object query)
        {
            Events.Add("end");
            if (EndFailure) throw new InvalidOperationException("native End failure");
        }
        public int GetData(object query, Span<byte> data, uint flags)
        {
            Assert.Equal(1u, flags);
            Assert.Equal(88, data.Length);
            Reads++;
            for (int i = 0; i < 11; i++)
            {
                ulong value = i == 10 ? 9007199254740993ul : (ulong)i;
                MemoryMarshal.Write(data[(i * 8)..], in value);
            }
            return Result;
        }
        public void Destroy(object query)
        {
            Events.Add("destroy");
            if (DestroyFailure) throw new InvalidOperationException("native Destroy failure");
        }
    }
}
