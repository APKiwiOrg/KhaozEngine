using System;
using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu.Performance;

public sealed partial class GpuPerfTimestampRingTests
{
    sealed class Rig : IDisposable
    {
        internal readonly GpuPerfTimestampTestDevice Device = new();
        internal readonly GpuPerfTimestampTestCommands Commands = new();
        internal readonly GpuFrameSubmission Submission;
        internal readonly GpuPerfTimestampRing Ring;
        internal readonly GpuPerfTimestampResult?[] Results;
        internal readonly int[] Publications;
        internal Action<int, GpuPerfTimestampResult>? OnPublish { get; set; }
        internal Rig(uint markerCount = 2, int sampleCapacity = 12)
        {
            Results = new GpuPerfTimestampResult?[sampleCapacity];
            Publications = new int[sampleCapacity];
            Submission = new(Device);
            Ring = new(Device, sampleCapacity, markerCount, (index, result) =>
            {
                Publications[index]++;
                OnPublish?.Invoke(index, result);
                Results[index] = result;
            });
        }
        internal void Begin(int index, uint markerCount = 2)
        {
            Submission.Begin(); Commands.Begin();
            Assert.True(Ring.TryBegin(Commands, Submission, index, markerCount, out var pool));
            Assert.Same(Ring.ActivePool, pool);
        }
        internal void Finish(uint markerCount)
        {
            var pool = Ring.ActivePool!;
            for (uint i = 1; i + 1 < markerCount; i++) Commands.WriteTimestamp(pool, i);
            Commands.FinishTimestampsOnEnd(pool, markerCount - 1);
            Commands.End();
        }
        internal void Record(int index, uint markerCount = 2)
        { Begin(index, markerCount); Finish(markerCount); Submission.Submit(Commands); }
        public void Dispose() { Submission.Dispose(); Ring.Dispose(); Commands.Dispose(); Device.Dispose(); }
    }
}
