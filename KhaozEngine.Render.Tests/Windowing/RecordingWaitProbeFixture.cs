using System;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.Windowing;

namespace KhaozEngine.Tests.Windowing;

internal sealed class RecordingWaitProbeFixture : IDisposable
{
    internal readonly FakeGpuDevice Gpu = new();
    readonly PostChainRig.Output _output;
    internal readonly FrameOutputDevice Device;
    internal readonly RecordingGpuCommandList Prefix;
    internal readonly RecordingGpuCommandList Main;
    internal readonly ProbeSource PrefixSource = new(RecordingWaitProbeTestData.PrefixList);
    internal readonly ProbeSource MainSource = new(RecordingWaitProbeTestData.MainList);
    internal readonly Frame Frame = new();
    internal RecordingWaitProbeFixture()
    {
        _output = new(Gpu, 32, 24);
        Device = new(Gpu, _output) { WaitSource = MainSource };
        Prefix = Wrap(PrefixSource); Main = Wrap(MainSource);
        Frame.Commands = Main; Frame.Width = 32; Frame.Height = 24;
        Frame.BeginRecordingWaitCallback(RecordingWaitProbeTestData.Request());
    }
    RecordingGpuCommandList Wrap(ProbeSource source) => new(Gpu.Factory.CreateCommandList())
    { WaitSource = source, AfterProbeBegin = source.Begin, AfterProbeEnd = source.End };
    internal void Observe(RecordingWaitRole role)
    {
        var list = role == RecordingWaitRole.Prefix ? Prefix : Main;
        var ticket = Frame.BeginRecordingWaitObservation(role, Device, list);
        using (GpuRecording.Open(Device, list, "recording-wait test")) Frame.CompleteRecordingWaitObservation(ticket);
    }
    internal void PrefixSubmission()
    {
        Frame.DeclareRecordingWaitPrefix(true); Observe(RecordingWaitRole.Prefix);
        Device.Submit(Prefix); Frame.RecordPreparePhaseSubmission();
    }
    public void Dispose() { Frame.DisposeSubmission(); Main.Dispose(); Prefix.Dispose(); Device.Dispose(); _output.Dispose(); Gpu.Dispose(); }

    internal sealed class ProbeSource(Guid list) : IGpuRecordingWaitSource, IGpuRingWaitSource
    {
        internal Guid DeviceId = RecordingWaitProbeTestData.Device;
        internal Guid ListId = list;
        internal ulong Attempt;
        internal bool Recording;
        internal bool Available = true;
        internal int Reads;
        internal string? ThrowAt;
        internal GpuRecordingWaitReceipt? Override { get; set; }
        void Read(string point) { Reads++; if (ThrowAt == point) throw new InvalidOperationException("diagnostic " + point); }
        internal void Begin() { Attempt = unchecked(Attempt + 1); Recording = true; }
        internal void End() => Recording = false;
        public Guid RecordingWaitListIdentity { get { Read("list"); return ListId; } }
        public Guid RingWaitDeviceIdentity { get { Read("device"); return DeviceId; } }
        public bool TryGetRecordingAttempt(out ulong attempt) { Read("attempt"); attempt = Attempt; return Available; }
        public bool TryGetRecordingWait(out GpuRecordingWaitReceipt receipt)
        {
            Read("receipt");
            receipt = Override ?? new(DeviceId, ListId, Attempt, ListId == RecordingWaitProbeTestData.PrefixList ? 1UL : 2UL, 0, 2, 0, 0, 0, 1000);
            return Available && Recording;
        }
        public GpuRingWaitSnapshotStatus TryReadRingWaitTotals(out GpuRingWaitTotals totals)
        { Read("snapshot"); totals = new(DeviceId, 2, 1000, 0, 0); return Available ? GpuRingWaitSnapshotStatus.Available : GpuRingWaitSnapshotStatus.Unavailable; }
    }
}
