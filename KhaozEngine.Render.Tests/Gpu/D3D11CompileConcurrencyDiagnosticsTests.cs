using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Gpu.D3D11;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed class D3D11CompileConcurrencyDiagnosticsTests
{
    [Fact]
    public async Task HeldWorkers_RecordMonotonicIntervalsAndPeakBeforeTheyFinish()
    {
        const int count = 3;
        using var entered = new CountdownEvent(count);
        using var release = new ManualResetEventSlim();
        var pool = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, count);
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        var compiles = new D3D11ShaderCompilation<int>[count];
        long before = Stopwatch.GetTimestamp();
        using (diagnostics.Use())
        {
            for (int i = 0; i < count; i++)
                compiles[i] = D3D11ShaderCompilation<int>.Start(() =>
                {
                    using var measurement = D3D11ShaderCompileDiagnostics.MeasureCompile(
                        "key", "program", D3D11ShaderStage.Fragment, "abc");
                    entered.Signal();
                    release.Wait();
                    return 1;
                }, scheduler: pool.ConcurrentScheduler);
        }
        try
        {
            entered.Wait();
            Assert.Empty(diagnostics.GetRecords());
            Assert.Equal(count, diagnostics.GetSummary().PeakConcurrency);
        }
        finally
        {
            release.Set();
            foreach (var compile in compiles) Assert.Equal(1, compile.Join());
            pool.Complete();
            await pool.Completion;
        }
        long after = Stopwatch.GetTimestamp();
        var records = diagnostics.GetRecords();
        Assert.Equal(count, records.Length);
        Assert.All(records, record =>
        {
            Assert.InRange(record.StartedTimestamp!.Value, before, after);
            Assert.InRange(record.EndedTimestamp!.Value, record.StartedTimestamp.Value, after);
            Assert.Equal(Stopwatch.GetElapsedTime(record.StartedTimestamp.Value,
                record.EndedTimestamp.Value).TotalMilliseconds, record.Milliseconds);
        });
        Assert.True(records.Max(r => r.StartedTimestamp) <= records.Min(r => r.EndedTimestamp));
        var summary = diagnostics.GetSummary();
        Assert.Equal(count, summary.PeakConcurrency);
        Assert.Equal(records.Sum(r => r.Milliseconds), summary.SummedMilliseconds);
        Assert.Equal(Stopwatch.GetElapsedTime(records.Min(r => r.StartedTimestamp!.Value),
            records.Max(r => r.EndedTimestamp!.Value)).TotalMilliseconds, summary.ElapsedMilliseconds);
    }

    [Fact]
    public void CacheHitsDoNotCountAsCompiles_AndFailedAttemptsCloseTheirInterval()
    {
        var diagnostics = new D3D11ShaderCompileDiagnostics();
        using (diagnostics.Use())
        {
            D3D11ShaderCompileDiagnostics.RecordCacheHit("cached", "program", D3D11ShaderStage.Vertex);
            Assert.Equal(0, diagnostics.GetSummary().PeakConcurrency);
            var failure = new InvalidOperationException("FXC error");
            Assert.Same(failure, Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using var measurement = D3D11ShaderCompileDiagnostics.MeasureCompile(
                    "bad", "program", D3D11ShaderStage.Fragment, "abc");
                throw failure;
            })));
            using (D3D11ShaderCompileDiagnostics.MeasureCompile(
                "next", "program", D3D11ShaderStage.Fragment, "abc")) { }
        }
        Assert.Equal(1, diagnostics.GetSummary().PeakConcurrency);
        var records = diagnostics.GetRecords();
        Assert.Null(records[0].StartedTimestamp);
        Assert.Null(records[0].EndedTimestamp);
        Assert.All(records.Skip(1), record => Assert.True(record.EndedTimestamp >= record.StartedTimestamp));
    }
}
