using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Windowing;

[Collection("AllocSensitive")]
public sealed class FrameDiagnosticsGBufferGateTests
{
    [Fact]
    public void Three_cycles_share_neighbouring_baselines_and_hold_the_last_baseline_to_the_end()
    {
        var source = new GateSource();
        var schedule = new FrameDiagnosticsGBufferGateSchedule(source, 1150);
        schedule.Boundary(0, 100);
        long allocated;
        using (AllocationRegion.Start("G-buffer gate callback schedule"))
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int callback = 0; callback < 1150; callback++)
            {
                source.Render();
                schedule.CallbackEnded(callback);
                if (callback < 1149) schedule.Boundary(callback + 1, 101 + callback);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, allocated);
        schedule.End(1150, 1250);
        var report = schedule.Report(1150);
        Assert.Equal(36, report.BlockCallbacks);
        Assert.Equal(6, report.DiscardCallbacksPerBlock);
        Assert.Equal(31, report.Schedule.Length);
        Assert.Equal(15, report.Pairs.Length);
        for (int block = 0; block < 31; block++)
        {
            var recorded = report.Schedule[block];
            int gate = block % 2 == 0 ? 0 : block / 2 % 5 + 1;
            Assert.Equal(block * 36, recorded.StartCallback);
            Assert.Equal(block == 30 ? 1150 : (block + 1) * 36, recorded.EndCallbackExclusive);
            Assert.Equal(gate, recorded.GateId);
            Assert.Equal(36, recorded.BlockCallbacks);
        }
        for (int pair = 0; pair < 15; pair++)
        {
            Assert.Equal(pair * 2, report.Pairs[pair].BaselineBeforeBlock);
            Assert.Equal(pair * 2 + 1, report.Pairs[pair].GateBlock);
            Assert.Equal(pair * 2 + 2, report.Pairs[pair].BaselineAfterBlock);
        }
        Assert.Equal(5, report.Gates.Length);
        Assert.All(report.Gates, gate => Assert.NotEmpty(gate.SourceMethods));
        Assert.Equal(6, report.Counts.Length);
        Assert.All(report.Counts.Where(c => c.GateId != 0), count =>
        {
            Assert.Equal(3, count.Blocks);
            Assert.Equal(108, count.ScheduledCallbacks);
            Assert.Equal(108, count.MatchingCallbacks);
            Assert.Equal(216, count.SkippedDraws);
        });
        Assert.Equal(1150, report.AppliedPerCallback.Length);
        Assert.Equal(1150, report.SkippedDrawsPerCallback.Length);
        Assert.All(report.AppliedPerCallback.Take(36), gate => Assert.Equal(0, gate));
        Assert.All(report.AppliedPerCallback.Skip(36).Take(36), gate => Assert.Equal(1, gate));
        Assert.All(report.AppliedPerCallback.Skip(1080), gate => Assert.Equal(0, gate));
        Assert.Equal(GBufferGate.Disabled, source.Active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Recorder_applies_only_inside_sampling_and_serializes_completed_callbacks(int ending)
    {
        string directory = Path.Combine("/tmp/khaozengine-mac-frame-performance/gbuffer-category-gates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "result.json");
            var source = new GateSource();
            using var recorder = new FrameDiagnosticsRecorder(path, 115, gbufferGates: source);
            var window = new FrameDiagnosticsWindowState(true, false, false, 800, 600, 0, true);
            recorder.Observe(0, 0, window, default);
            Assert.Equal(GBufferGate.Disabled, source.Active);
            File.WriteAllText(path + ".start", "");
            recorder.Observe(0, 115, window, default);
            Assert.Equal(GBufferGate.Disabled, source.Active);
            recorder.Observe(0, 116, window, default);
            Assert.Equal(GBufferGate.None, source.Active);
            int callbacks = ending != 0 ? 40 : 1150;
            for (int callback = 0; callback < callbacks; callback++)
            {
                if (callback != 37) source.Render(); // A suppressed render must not repeat the last applied gate.
                recorder.Observe(1.0 / 115, 117 + callback, window, default);
            }
            if (ending != 0)
            {
                Assert.Equal(GBufferGate.Rigid, source.Active);
                if (ending == 1) recorder.Dispose();
                else
                {
                    recorder.Observe(0, 156, window, default);
                    Assert.NotNull(recorder.Failure);
                }
                Assert.False(File.Exists(path));
            }
            else
            {
                await recorder.Publication;
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                var root = json.RootElement;
                Assert.Equal(14, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(10, root.GetProperty("durationSeconds").GetDouble());
                Assert.False(root.GetProperty("passObserverRegistered").GetBoolean());
                var gates = root.GetProperty("gbufferGates");
                Assert.Equal(36, gates.GetProperty("blockCallbacks").GetInt32());
                Assert.Equal(6, gates.GetProperty("discardCallbacksPerBlock").GetInt32());
                Assert.Equal(5, gates.GetProperty("gates").GetArrayLength());
                Assert.Equal(31, gates.GetProperty("schedule").GetArrayLength());
                Assert.Equal(15, gates.GetProperty("pairs").GetArrayLength());
                Assert.Equal(1150, gates.GetProperty("appliedPerCallback").GetArrayLength());
                Assert.Equal(-1, gates.GetProperty("appliedPerCallback")[37].GetInt32());
                Assert.Equal(0, gates.GetProperty("skippedDrawsPerCallback")[37][0].GetInt64());
                Assert.Equal(214, gates.GetProperty("counts")[1].GetProperty("skippedDraws").GetInt64());
                Assert.Equal(1, gates.GetProperty("counts")[1].GetProperty("noFrameCallbacks").GetInt32());
            }
            Assert.Equal(GBufferGate.Disabled, source.Active);
            recorder.Observe(1, 2000, window, default);
            Assert.Equal(GBufferGate.Disabled, source.Active);
        }
        finally { Directory.Delete(directory, true); }
    }

    sealed class GateSource : IFrameDiagnosticsGBufferGates
    {
        internal GBufferGate Active = GBufferGate.Disabled;
        int _applied = -1;
        internal void Render() => _applied = (int)Active;
        public void Apply(GBufferGate gate) => Active = gate;
        public int ConsumeApplied(Span<long> skippedDraws)
        {
            skippedDraws.Clear();
            int applied = _applied;
            if (applied > 0) skippedDraws[applied - 1] = 2;
            _applied = -1;
            return applied;
        }
    }
}
