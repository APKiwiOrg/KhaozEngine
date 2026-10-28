using System;
using System.Collections.Immutable;

namespace KhaozEngine.Gpu.Internal
{
    /// <summary>
    /// Optional retained-completion wait for a timestamp pool whose query data can trail its completion fence.
    /// Direct3D 11 implements it. Vulkan query results are available at fence completion and do not.
    /// </summary>
    internal interface IGpuTimestampAvailabilityWait
    {
        /// <summary>
        /// After the caller's completion drain, wait until every query of the ended batch reports its data
        /// available, or until <paramref name="now"/> reaches <paramref name="deadline"/>. Never reads or invents
        /// a value. <see cref="IGpuTimestampPool.TryResolve"/> still decides Ready, Invalid or Pending.
        /// </summary>
        GpuTimestampAvailability AwaitAvailability(IGpuFence completion, Func<long> now, long deadline);
    }

    /// <summary>
    /// The evidence of one availability wait. <see cref="Polls"/> counts flushing rounds after the first
    /// non-flushing read, so a batch that was already available records zero. Ticks use the caller's clock.
    /// <see cref="Queries"/> is empty when the completion fence had not signalled and nothing was read.
    /// </summary>
    internal sealed record GpuTimestampAvailability(bool Ready, int Polls, long StartTicks, long EndTicks,
        ulong FenceTarget, ulong FenceCompleted, ulong DrainTarget, ImmutableArray<GpuTimestampQueryAvailability> Queries);

    /// <summary>
    /// One query's read sequence. Every read before the last returned <see cref="NotReady"/> times S_FALSE,
    /// the first of them non-flushing, and the last read returned <see cref="FinalResult"/>. A failing
    /// HRESULT throws instead, so this pair is the complete sequence.
    /// </summary>
    internal readonly record struct GpuTimestampQueryAvailability(string Query, int NotReady, int FinalResult);
}
