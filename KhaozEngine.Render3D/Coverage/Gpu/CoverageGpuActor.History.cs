using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D;

/// <summary>Keeps a submitted surface alive through cold replacement and abandoned migration.</summary>
internal sealed partial class CoverageGpuActor
{
    internal CoverageGpuBatch? HistoryBatch { get; private set; }
    internal CoverageGpuHistoryLease FrameHistoryLease = null!;
    CoverageGpuHistoryLease? historyLease;
    IGpuBuffer? historyBuffer;
    CoverageGpuReservation historyReservation;

    internal void CopyHistory(IGpuCommandList commands, CoverageGpuBatch batch, CoverageGpuReservation destination)
    {
        if (ReferenceEquals(historyBuffer, batch.Previous)) return;
        uint words = checked(16u + (uint)(historyReservation.SourceCount + historyReservation.VertexCount) * 16u);
        commands.CopyBuffer(historyBuffer!, CoverageGpuLayout.Bytes(historyReservation.CurrentBase), batch.Previous,
            CoverageGpuLayout.Bytes(destination.PreviousBase), CoverageGpuLayout.Bytes(words));
    }

    internal void CommitHistory(CoverageGpuBatch batch, CoverageGpuReservation reservation)
    {
        if (!ReferenceEquals(HistoryBatch, batch))
        {
            FrameHistoryLease.Activate();
            historyLease?.Retire();
            historyLease = FrameHistoryLease;
            HistoryBatch = batch;
        }
        historyBuffer = batch.Current;
        historyReservation = reservation;
    }

    internal void ReleaseHistory()
    {
        historyLease?.Retire();
        historyLease = null; HistoryBatch = null; historyBuffer = null;
    }
}
