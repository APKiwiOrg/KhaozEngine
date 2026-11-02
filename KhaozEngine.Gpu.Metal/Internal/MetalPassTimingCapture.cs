using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace KhaozEngine.Gpu.Metal.Internal;

internal sealed class MetalPassTimingCapture : IDisposable
{
    internal const int BlockCallbacks = 72;
    readonly IMetalPassTimingNative _native;
    readonly Slot[] _slots;
    readonly Command[] _commands = Enumerable.Range(0, 64).Select(_ => new Command()).ToArray();
    readonly Dictionary<PassTimingSignature, int> _signatures;
    readonly int _signatureCapacity, _sampleCapacity;
    // About 48 encoders a frame over all 4096 callbacks, far above the observed 17 to 20 a frame. Records are about
    // 170 bytes each.
    readonly FrameTimingBuffer<PassTimingEncoder> _records = new(196608);
    readonly FrameTimingBuffer<PassTimingClockPair> _clocks = new(64);
    readonly PassTimingBlock[] _blocks = new PassTimingBlock[58];
    readonly MetalPassLabels _labels;
    readonly FrameTimingBuffer<PassTransfer> _transfers;
    readonly MetalCascadeDiscardExperiment _experiment;
    int _blockCount, _disposed;
    bool _timed, _discardBlock;
    // The frame recording the label stack describes. Bound by the first label event or encoder of each recording.
    IntPtr _frameBuffer, _frameEncoder;
    int _frameCallback = -1, _frameOrdinal;
    // Where a tracked Unknown encoder's record waits for its resolution, when it is timed.
    Slot? _discardSlot;
    int _discardPending;
    long _capacityOverflow, _signatureOverflow, _untimed, _skipped, _commandOverflow;
    long _resolveFailures, _errorValues, _clockFailures, _nativeFailures;
    internal IntPtr DrawableTexture;
    internal bool Supported { get; }
    internal string? Reason { get; }

    // A frame carries a few hundred label events, so the event bound covers 1400 callbacks at about 370 a callback.
    // Events are about 32 bytes each and transfers about 48, so the two together preallocate about 25 MB.
    internal MetalPassTimingCapture(IMetalPassTimingNative native, int slots = 16, int samples = 4096,
        int signatures = 256, int labels = 256, int labelEvents = 524288, int transfers = 131072)
    {
        if (slots < 1 || slots > 32 || samples < 4 || samples > 4096 || signatures < 1 || signatures > 256)
            throw new ArgumentOutOfRangeException(nameof(slots));
        _native = native;
        _labels = new(labels, labelEvents);
        _transfers = new(transfers);
        _experiment = new(native);
        _sampleCapacity = samples;
        _signatureCapacity = signatures;
        _signatures = new(signatures);
        _signatures.Add(default, -1);
        _signatures.Clear(); // Warm the value comparer before sampling.
        _slots = new Slot[slots];
        if (!native.Supported) { Reason = native.Reason ?? "Stage boundary counters unavailable."; return; }
        try
        {
            for (int i = 0; i < slots; i++) _slots[i] = new(native.Create(samples), samples);
            native.SampleClocks(out _, out _); // Warm the clock interop before arming.
            Supported = true;
        }
        catch (Exception error)
        {
            Reason = "Pass timing creation failed: " + error.Message;
            foreach (var slot in _slots) if (slot is not null) native.Release(slot.Resources);
            Array.Clear(_slots);
        }
    }

    internal void Boundary(int callback, long timestamp)
    {
        if (callback % BlockCallbacks != 0) return;
        CloseBlock(callback, timestamp);
        // Timing stays on in every block so pass times compare across the discard states, which alternate starting off.
        _timed = true;
        _discardBlock = MetalCascadeDiscardExperiment.DiscardBlock(callback, BlockCallbacks);
        if (_blockCount < _blocks.Length)
            _blocks[_blockCount++] = new(callback, callback, timestamp, timestamp, _timed);
        Clock(callback, timestamp);
    }

    internal void End(int callback, long timestamp)
    {
        CloseBlock(callback, timestamp);
        _timed = false;
        _discardBlock = false;
        Clock(callback, timestamp);
    }

    void CloseBlock(int callback, long timestamp)
    {
        if (_blockCount > 0)
            _blocks[_blockCount - 1] = _blocks[_blockCount - 1] with { EndCallbackExclusive = callback, EndTimestamp = timestamp };
    }

    void Clock(int callback, long timestamp)
    {
        if (!Supported) return;
        try
        {
            long before = Stopwatch.GetTimestamp();
            _native.SampleClocks(out ulong cpu, out ulong gpu);
            _clocks.Add(new(callback, timestamp, before, Stopwatch.GetTimestamp(), cpu, gpu));
        }
        catch { Interlocked.Increment(ref _clockFailures); }
    }

    // Only the callback thread records. A driver thread only claims completed commands below.
    internal IntPtr Begin(MetalEncoderKind kind, IntPtr buffer, IntPtr descriptor, int callback, bool sampling)
    {
        PassTimingResources resources = default;
        int first = 0;
        Slot? slot = null;
        Command? command = null;
        int signature = -1, ordinal = 0;
        bool timed = Supported && sampling && _timed && callback >= 0 && Volatile.Read(ref _disposed) == 0;
        bool frame = Supported && sampling && callback >= 0 && callback == _frameCallback && buffer == _frameBuffer;
        int label = -1, occurrence = 0;
        PassDepthStencil planes = default;
        bool directional = false;
        try
        {
            if (Supported && sampling && callback >= 0 && _native.Owns(buffer))
            {
                command = FindOrClaim(buffer);
                if (command is not null)
                {
                    ordinal = command.Ordinal++;
                    if (timed && command.Slot == -2) ClaimSamples(command);
                    if (timed && command.Slot >= 0)
                    {
                        slot = _slots[command.Slot];
                        int needed = kind == MetalEncoderKind.Render ? 4 : 2;
                        if (slot.Used + needed <= _sampleCapacity)
                        {
                            first = slot.Used;
                            slot.Used += needed;
                            resources = slot.Resources;
                            if (kind == MetalEncoderKind.Render) signature = Signature(descriptor);
                        }
                        else { Interlocked.Increment(ref _capacityOverflow); slot = null; }
                    }
                }
            }
            if (frame)
            {
                _labels.Innermost(out label, out occurrence);
                // The whole stack rather than the innermost label: the encoder opens on the first draw, which is
                // inside a nested shadow.rigid.* or shadow.cascade.* scope.
                if (kind == MetalEncoderKind.Render && _labels.DirectionalOpen)
                {
                    planes = _native.DepthStencil(descriptor);
                    directional = planes.Any;
                }
            }
        }
        catch { Interlocked.Increment(ref _nativeFailures); slot = null; resources = default; directional = false; }
        if (resources.Samples == IntPtr.Zero) Interlocked.Increment(ref _untimed);
        if (directional && !_discardBlock) _experiment.CountDirectionalOff();
        // After the signature read, so a descriptor's signature and id stay the same in both discard states.
        bool unknown = directional && _discardBlock && _experiment.Prepare(descriptor, planes);
        // Native.Begin also clears render attachments on the ordinary path. The native sink calls this
        // only during sampling, and timed render attachments are cleared in its finally before returning.
        IntPtr encoder;
        try { encoder = _native.Begin(kind, buffer, descriptor, resources, first); }
        finally { if (unknown) _experiment.Restore(descriptor, planes); }
        if (frame)
        {
            _frameOrdinal = command is null ? _frameOrdinal + 1 : ordinal + 1;
            _frameEncoder = encoder;
        }
        bool tracked = unknown && encoder != IntPtr.Zero;
        if (tracked)
        {
            _experiment.Opened(encoder, planes);
            _discardSlot = null;
        }
        if (slot is not null && command is not null && resources.Samples != IntPtr.Zero)
        {
            if (tracked) { _discardSlot = slot; _discardPending = slot.Count; }
            slot.Pending[slot.Count++] = new(buffer.ToInt64(), callback, ordinal, kind, signature, first, encoder != IntPtr.Zero,
                label, occurrence, directional, !directional ? null : tracked ? PassStoreResolution.Unresolved : PassStoreResolution.DescriptorStore);
        }
        return encoder;
    }

    /// <summary>Called on every render encoder's end, sampling or not, because an encoder opened Unknown must be
    /// resolved on every path. Holds nothing that disposal releases.</summary>
    internal void BeforeEndEncoding(IntPtr encoder, bool sampling)
    {
        if (_experiment.BeforeEndEncoding(encoder, sampling) is not { } resolution || _discardSlot is not { } slot) return;
        slot.Pending[_discardPending] = slot.Pending[_discardPending] with { StoreResolution = resolution };
        _discardSlot = null;
    }

    /// <summary>Starts a new label stack when the frame list moves to another recording or callback.</summary>
    internal void BindFrame(int callback, IntPtr buffer)
    {
        if (callback == _frameCallback && buffer == _frameBuffer) return;
        _labels.BeginRecording();
        _frameCallback = callback;
        _frameBuffer = buffer;
        _frameEncoder = IntPtr.Zero;
        _frameOrdinal = 0;
    }

    internal void ObservePass(IntPtr buffer, int callback, PassLabelEventKind kind, string id, int occurrence)
    {
        BindFrame(callback, buffer);
        int label = _labels.Observe(callback, buffer.ToInt64(), _frameOrdinal, kind, id, occurrence);
        if (kind == PassLabelEventKind.End && label == MetalPassLabels.DirectionalLabel && !_labels.DirectionalOpen)
            _experiment.ScopeEnded();
    }

    internal void ForeignPass() => _labels.Foreign();
    internal void PassObserverFailed() => _labels.ObserverFailed();

    /// <summary>Records one blit operation when <paramref name="encoder"/> is the frame list's open encoder.</summary>
    internal void Transfer(IntPtr encoder, int callback, string kind, ulong bytes, IntPtr destination)
    {
        if (encoder == IntPtr.Zero || encoder != _frameEncoder || callback != _frameCallback) return;
        _labels.Innermost(out int label, out int occurrence);
        _transfers.Add(new(callback, _frameBuffer.ToInt64(), _frameOrdinal - 1, kind, bytes, destination.ToInt64(), label, occurrence));
    }

    int Signature(IntPtr descriptor)
    {
        var signature = _native.Signature(descriptor, DrawableTexture);
        if (_signatures.TryGetValue(signature, out int id)) return id;
        if (_signatures.Count == _signatureCapacity) { Interlocked.Increment(ref _signatureOverflow); return -1; }
        id = _signatures.Count;
        _signatures.Add(signature, id);
        return id;
    }

    Command? FindOrClaim(IntPtr buffer)
    {
        foreach (var command in _commands)
            if (Volatile.Read(ref command.State) == 1 && command.Handle == buffer) return command;
        foreach (var command in _commands)
        {
            if (Interlocked.CompareExchange(ref command.State, -1, 0) != 0) continue;
            command.Handle = buffer;
            command.Ordinal = 0;
            command.Slot = -2; // No timed encoder yet. A sample buffer is claimed on the first one.
            Volatile.Write(ref command.State, 1);
            return command;
        }
        Interlocked.Increment(ref _commandOverflow);
        return null;
    }

    void ClaimSamples(Command command)
    {
        command.Slot = -1;
        for (int i = 0; i < _slots.Length; i++)
            if (Interlocked.CompareExchange(ref _slots[i].State, 1, 0) == 0)
            {
                command.Slot = i;
                _slots[i].Used = _slots[i].Count = 0;
                return;
            }
        Interlocked.Increment(ref _skipped);
    }

    internal void Complete(IntPtr buffer, bool sampling)
    {
        foreach (var command in _commands)
        {
            if (Volatile.Read(ref command.State) != 1 || command.Handle != buffer
                || Interlocked.CompareExchange(ref command.State, 2, 1) != 1) continue;
            try
            {
                if (command.Slot >= 0)
                {
                    Slot slot = _slots[command.Slot];
                    try { if (sampling) Resolve(slot); }
                    finally
                    {
                        Volatile.Write(ref slot.State, 0);
                        if (Volatile.Read(ref _disposed) != 0) Release(slot);
                    }
                }
            }
            finally { Volatile.Write(ref command.State, 0); }
            return;
        }
    }

    void Resolve(Slot slot)
    {
        bool resolved = false;
        try { _native.Resolve(slot.Resources.Samples, slot.Used, slot.Values); resolved = true; }
        catch { Interlocked.Increment(ref _resolveFailures); }
        for (int i = 0; i < slot.Count; i++)
        {
            var pending = slot.Pending[i];
            int n = pending.Kind == MetalEncoderKind.Render ? 4 : 2;
            ulong? a = null, b = null, c = null, d = null;
            if (resolved)
            {
                a = Value(slot.Values[pending.First]);
                b = Value(slot.Values[pending.First + 1]);
                if (n == 4) { c = Value(slot.Values[pending.First + 2]); d = Value(slot.Values[pending.First + 3]); }
            }
            string status = !pending.Created ? "encoderUnavailable" : !resolved ? "resolveFailure"
                : a is null || b is null || (n == 4 && (c is null || d is null)) ? "missing"
                : a == 0 || b < a || (n == 4 && (c == 0 || d < c)) ? "invalid" : "valid";
            _records.Add(new(pending.Buffer, pending.Callback, pending.Ordinal,
                pending.Kind == MetalEncoderKind.Render ? "render" : pending.Kind == MetalEncoderKind.Compute ? "compute" : "blit",
                pending.Signature, n == 4 ? a : null, n == 4 ? b : null, c, d,
                n == 2 ? a : null, n == 2 ? b : null, status, pending.LabelId, pending.LabelOccurrence,
                pending.DirectionalScope, pending.StoreResolution));
        }
    }

    ulong? Value(ulong value)
    {
        if (value != ulong.MaxValue) return value;
        Interlocked.Increment(ref _errorValues);
        return null;
    }

    internal PassTimingSnapshot Snapshot() => new(Supported, Reason, "atStageBoundary", Supported ? _slots.Length : 0,
        _sampleCapacity, BlockCallbacks, "native GPU ticks, measured nanoseconds per tick in clockSegments",
        "cpuNs = cpu0 + (gpu - gpu0) * (cpu1 - cpu0) / (gpu1 - gpu0), using the nearest valid adjacent clock pair",
        _blocks.Take(_blockCount).ToArray(), _clocks.Snapshot(), _signatures.OrderBy(p => p.Value).Select(p => p.Key).ToArray(),
        _records.Snapshot(), _capacityOverflow, _signatureOverflow, _untimed, _skipped, _commandOverflow,
        _resolveFailures, _errorValues, _clockFailures, _nativeFailures, _commands.Count(c => Volatile.Read(ref c.State) != 0));

    internal PassLabelsSnapshot LabelsSnapshot() => _labels.Snapshot();
    internal FrameTimingBufferSnapshot<PassTransfer> TransfersSnapshot() => _transfers.Snapshot();
    internal PassExperimentSnapshot ExperimentSnapshot() => _experiment.Snapshot(BlockCallbacks, _blocks.Take(_blockCount)
        .Select(b => new PassExperimentBlock(b.StartCallback, b.EndCallbackExclusive, b.StartTimestamp, b.EndTimestamp,
            MetalCascadeDiscardExperiment.DiscardBlock(b.StartCallback, BlockCallbacks))).ToArray());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var slot in _slots) if (slot is not null) Release(slot);
    }

    void Release(Slot slot)
    {
        if (Interlocked.CompareExchange(ref slot.State, 3, 0) == 0) _native.Release(slot.Resources);
    }

    sealed class Command { internal int State, Slot, Ordinal; internal IntPtr Handle; }
    sealed class Slot(PassTimingResources resources, int samples)
    {
        internal readonly PassTimingResources Resources = resources;
        internal readonly ulong[] Values = new ulong[samples];
        internal readonly Pending[] Pending = new Pending[samples / 2];
        internal int State, Used, Count;
    }
    readonly record struct Pending(long Buffer, int Callback, int Ordinal, MetalEncoderKind Kind,
        int Signature, int First, bool Created, int LabelId, int LabelOccurrence, bool DirectionalScope, string? StoreResolution);
}
