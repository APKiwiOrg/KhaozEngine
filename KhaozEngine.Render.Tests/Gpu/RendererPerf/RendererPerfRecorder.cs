using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using KhaozEngine.Gpu;
using KhaozEngine.Tests.Gpu.Performance;

namespace KhaozEngine.Tests.Gpu.RendererPerf;

internal enum RendererPerfCounter
{
    BufferUploadBytes, BufferUpdates, DeviceBufferUpdates, CommandBufferUpdates, TextureUploadBytes, TextureUpdates,
    RequestedBufferBytes, RequestedTextureBytes, ResourceCreations, BufferCreations, TextureCreations,
    Draws, IndexedDraws, Vertices, Indices, Instances, Triangles, UnknownTopologyDraws,
    Dispatches, DispatchGroups, DispatchGroupsX, DispatchGroupsY, DispatchGroupsZ, DispatchInvocations, UnknownLocalSizeDispatches,
    BufferCopies, TextureCopies, TextureSubresourceCopies, CopyBytes, UnknownCopyBytes,
    Resolves, ResolveReadBytes, ResolveWriteBytes, MipGenerations, ColorClears, DepthClears,
    FramebufferBinds, GraphicsPipelineBinds, ComputePipelineBinds, GraphicsResources, ComputeResources,
    VertexBuffers, IndexBuffers, Scissors, FullScissors, RedundantFramebuffers, RedundantGraphicsPipelines,
    RedundantComputePipelines, RedundantGraphicsResources, RedundantComputeResources, RedundantVertexBuffers,
    RedundantIndexBuffers, RedundantScissors, RedundantFullScissors, Submissions, WaitForIdleCalls, Presents
}

// Marker indices describe a frozen, properly nested event order. Parent is an index, or -1 for a root.
internal readonly record struct RendererPerfPassDefinition(string Id, int Occurrence, int Parent, uint BeginMarker, uint EndMarker,
    bool DeviceOnly = false);

// Mutable numeric storage belongs to the recording thread. Only Harvest constructs immutable report objects.
internal sealed partial class RendererPerfRecorder
{
    static readonly string[] CounterNames = Enum.GetNames<RendererPerfCounter>();
    readonly Frame[] _frames;
    readonly RendererPerfPassDefinition[] _passes;
    readonly RendererPerfLayout _layout;
    readonly Func<long> _now;
    readonly long _frequency;
    readonly GpuPerfTimestampRing? _timestamps;
    readonly Func<long?>? _nativeStateCalls;
    readonly int[] _passStack;
    readonly Dictionary<string, int> _devicePasses = new(StringComparer.Ordinal);
    readonly string[] _cpuStack;
    readonly object?[] _resources;
    readonly ResourceFacts[] _facts;
    int _resourceCount, _frame = -1, _passDepth, _cpuDepth, _uploadDepth, _recordings;
    long _segmentStart;
    uint _nextMarker;
    IGpuTimestampPool? _pool;
    IGpuCommandList? _commands;
    GpuFrameSubmission? _submission;
    IGpuDevice? _device;

    internal RendererPerfRecorder(int sampleCapacity, RendererPerfLayout layout, RendererPerfPassDefinition[] passes,
        Func<long> now, long frequency, GpuPerfTimestampRing? timestamps = null, bool enabled = true,
        Func<long?>? nativeStateCalls = null, int cpuSpanCapacity = 1024, int resourceCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cpuSpanCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceCapacity);
        ArgumentNullException.ThrowIfNull(passes);
        ArgumentNullException.ThrowIfNull(now);
        if (passes.Length > 255) throw new ArgumentOutOfRangeException(nameof(passes));
        _passes = (RendererPerfPassDefinition[])passes.Clone();
        for (int i = 0; i < _passes.Length; i++)
            if (_passes[i].DeviceOnly) _devicePasses.Add(_passes[i].Id, i);
        ValidateLayout();
        _frames = new Frame[sampleCapacity + 1];
        for (int i = 0; i < _frames.Length; i++) _frames[i] = new(passes.Length, cpuSpanCapacity);
        _passStack = new int[passes.Length];
        _cpuStack = new string[cpuSpanCapacity];
        _resources = new object[resourceCapacity];
        _facts = new ResourceFacts[resourceCapacity];
        _layout = layout; _now = now; _frequency = frequency; _timestamps = timestamps;
        _nativeStateCalls = nativeStateCalls; Enabled = enabled;
    }

    internal bool Enabled { get; }
    internal RendererPerfConstructionCapture? Capture { get; private set; }
    internal void AttachCapture(RendererPerfConstructionCapture capture) => Capture = capture;
    internal ResourceFacts PipelineFacts(object resource, RendererPerfPipelineKind kind)
    {
        var facts = Facts(resource); Capture?.Bound(facts.OccurrenceId ?? -1, kind); return facts;
    }
    internal IGpuCommandList? FrameCommands { get; set; }
    internal string? FrameFailure(int sampleId) => _frames[sampleId + 1].Error;
    internal void UseSubmission(GpuFrameSubmission submission)
    {
        if (_frame < 0 || _commands is not null) throw new InvalidOperationException("Attach the host submission before recording.");
        _submission = submission;
    }
    internal uint MarkerCount => _layout == RendererPerfLayout.TotalOnly ? 2U : (uint)(_passes.Length - _devicePasses.Count) * 2 + 2;
    Frame Current => _frames[_frame + 1];
    internal void AttachDevice(IGpuDevice device) => _device = device;


    internal void BeginCpuPhase(string id)
    {
        if (!Enabled || _frame < 0) return;
        if (string.IsNullOrWhiteSpace(id) || _cpuDepth == _cpuStack.Length) { Fail("Invalid CPU phase or fixed CPU capacity exceeded."); return; }
        CloseCpuSegment(_now());
        _cpuStack[_cpuDepth++] = id;
        if (_layout == RendererPerfLayout.PerPass && _devicePasses.TryGetValue(id, out int pass))
        {
            if (Current.PassStates[pass] != 0 || _passes[pass].Parent != (_passDepth == 0 ? -1 : _passStack[_passDepth - 1]))
            { Fail("A device upload scope is outside its frozen owner."); return; }
            Current.PassStates[pass] = 1;
            _passStack[_passDepth++] = pass;
        }
    }

    internal void EndCpuPhase(string id)
    {
        if (!Enabled || _frame < 0) return;
        if (_cpuDepth == 0 || _cpuStack[_cpuDepth - 1] != id) { Fail("CPU phases ended out of order."); return; }
        if (_layout == RendererPerfLayout.PerPass && _devicePasses.TryGetValue(id, out int pass))
        {
            if (_passDepth == 0 || _passStack[_passDepth - 1] != pass)
            { Fail("Device upload scopes ended out of order."); return; }
            _passDepth--;
        }
        CloseCpuSegment(_now()); _cpuDepth--;
    }

    void CloseCpuSegment(long end)
    {
        var frame = Current;
        if (end < _segmentStart) { Fail("The CPU clock moved backwards."); return; }
        if (end != _segmentStart)
        {
            if (frame.CpuCount == frame.Cpu.Length) Fail("The fixed CPU span capacity was exceeded.");
            else frame.Cpu[frame.CpuCount++] = new(_cpuDepth == 0 ? "cpu.unattributed" : _cpuStack[_cpuDepth - 1],
                _cpuDepth < 2 ? "cpu.frameWall" : _cpuStack[_cpuDepth - 2], _segmentStart, end);
        }
        _segmentStart = end;
    }

    internal void BeginCommands(IGpuCommandList commands)
    {
        if (_frame < 0 || FrameCommands is not null && !ReferenceEquals(commands, FrameCommands)) return;
        if (++_recordings != 1) { Fail("An unexpected extra production recording occurred."); return; }
        _commands = commands;
        if (_timestamps is null) return;
        if (_submission is null) { Fail("The timestamp recording has no frame submission owner."); return; }
        if (!_timestamps.TryBegin(commands, _submission, _frame, MarkerCount, out _pool))
            Fail(_timestamps.Failure ?? "Timestamp recording was refused.");
    }

    internal void EndCommands(IGpuCommandList commands)
    {
        if (_frame < 0 || !ReferenceEquals(commands, _commands)) return;
        if (_nextMarker != MarkerCount - 1 || _passDepth != 0) Fail("The frozen marker schedule is incomplete.");
        // Preserve the partial native trace even after invalidation, without growing or reordering its layout.
        while (_nextMarker < MarkerCount - 1) WriteMarker(commands);
        if (_pool is not null) ((IGpuCommandListTimestampEnd)commands).FinishTimestampsOnEnd(_pool, MarkerCount - 1);
    }

    internal void BeginPass(IGpuCommandList commands, int pass)
    {
        if (!Enabled || _frame < 0 || _layout == RendererPerfLayout.TotalOnly) return;
        if (!ValidCommands(commands) || !ValidPass(pass) || _passes[pass].DeviceOnly || _passes[pass].BeginMarker != _nextMarker || Current.PassStates[pass] != 0
            || _passes[pass].Parent != (_passDepth == 0 ? -1 : _passStack[_passDepth - 1]))
        { Fail("An unexpected render pass occurrence or marker order was observed."); return; }
        Current.PassStates[pass] = 1;
        WriteMarker(commands); _passStack[_passDepth++] = pass;
        BeginCpuPhase(_passes[pass].Id);
    }

    internal void EndPass(IGpuCommandList commands, int pass)
    {
        if (!Enabled || _frame < 0 || _layout == RendererPerfLayout.TotalOnly) return;
        if (!ValidCommands(commands) || !ValidPass(pass) || _passDepth == 0 || _passStack[_passDepth - 1] != pass || _passes[pass].EndMarker != _nextMarker)
        { Fail("Render passes ended out of order."); return; }
        WriteMarker(commands); _passDepth--; EndCpuPhase(_passes[pass].Id);
    }

    internal void SkipPass(IGpuCommandList commands, int pass)
    {
        if (!Enabled || _frame < 0 || _layout == RendererPerfLayout.TotalOnly) return;
        if (!ValidCommands(commands) || !ValidPass(pass) || _passes[pass].BeginMarker != _nextMarker || Current.PassStates[pass] != 0
            || _passes[pass].Parent != (_passDepth == 0 ? -1 : _passStack[_passDepth - 1]))
        { Fail("A skipped pass is outside its frozen occurrence."); return; }
        var definition = _passes[pass];
        for (int i = 0; i < _passes.Length; i++)
            if (!_passes[i].DeviceOnly && _passes[i].BeginMarker >= definition.BeginMarker && _passes[i].EndMarker <= definition.EndMarker
                || _passes[i].DeviceOnly && IsDescendant(i, pass))
                Current.PassStates[i] = 2;
        while (_nextMarker <= definition.EndMarker) WriteMarker(commands);
    }

    bool ValidCommands(IGpuCommandList commands) => ReferenceEquals(RendererPerfCommandList.Unwrap(commands), _commands);
    bool ValidPass(int pass) => (uint)pass < (uint)_passes.Length;
    bool IsDescendant(int child, int parent)
    {
        for (int index = _passes[child].Parent; index >= 0; index = _passes[index].Parent)
            if (index == parent) return true;
        return false;
    }
    void WriteMarker(IGpuCommandList commands)
    {
        if (_pool is not null) commands.WriteTimestamp(_pool, _nextMarker);
        _nextMarker++;
    }

    internal void Fail(string reason)
    {
        Current.Error ??= reason;
        if (_frame >= 0) _timestamps?.InvalidateBlock(reason);
    }

    internal void Count(RendererPerfCounter counter, long value = 1)
    {
        if (!Enabled) return;
        int owner = _passDepth == 0 ? 0 : _passStack[_passDepth - 1] + 1;
        int index = owner * CounterNames.Length + (int)counter;
        Current.Counters[index] = checked(Current.Counters[index] + value);
    }

    internal long ReadCounter(int sampleId, RendererPerfCounter counter, int owner = -1)
    {
        var values = _frames[sampleId + 1].Counters;
        if (owner >= 0) return values[owner * CounterNames.Length + (int)counter];
        long total = 0;
        for (int i = (int)counter; i < values.Length; i += CounterNames.Length) total = checked(total + values[i]);
        return total;
    }

    internal void BeginUpload(long bytes, bool device)
    {
        if (_uploadDepth++ != 0) return;
        Count(RendererPerfCounter.BufferUpdates); Count(RendererPerfCounter.BufferUploadBytes, bytes);
        Count(device ? RendererPerfCounter.DeviceBufferUpdates : RendererPerfCounter.CommandBufferUpdates);
    }
    internal void EndUpload() => _uploadDepth--;
    internal static long UploadBytes<T>(int count) where T : unmanaged => checked((long)Unsafe.SizeOf<T>() * count);
    internal void TextureUpload(IGpuTexture texture, uint width, uint height)
    {
        Count(RendererPerfCounter.TextureUpdates);
        Count(RendererPerfCounter.TextureUploadBytes, checked((long)width * height * BytesPerPixel(texture.Format)));
    }

    internal void Register(object resource, ResourceFacts facts)
    {
        for (int i = 0; i < _resourceCount; i++)
            if (ReferenceEquals(_resources[i], resource)) { _facts[i] = facts; return; }
        if (_resourceCount == _resources.Length) { Fail("The fixed resource metadata capacity was exceeded."); return; }
        _resources[_resourceCount] = resource; _facts[_resourceCount++] = facts;
    }
    internal ResourceFacts Facts(object resource)
    {
        for (int i = 0; i < _resourceCount; i++) if (ReferenceEquals(_resources[i], resource)) return _facts[i];
        return default;
    }
    internal long? TextureBytes(IGpuTexture texture)
    {
        var facts = Facts(texture);
        if (!facts.Known) return null;
        long texels = 0;
        uint width = texture.Width, height = texture.Height;
        for (uint mip = 0; mip < texture.MipLevels; mip++)
        {
            texels = checked(texels + (long)width * height);
            width = Math.Max(1, width / 2); height = Math.Max(1, height / 2);
        }
        return checked(texels * facts.Layers * texture.SampleCount * BytesPerPixel(texture.Format));
    }
    internal void DispatchFacts(uint x, uint y, uint z, ResourceFacts facts)
    {
        if (_frame < 0) return;
        var frame = Current;
        if (frame.DispatchCount == frame.Dispatches.Length) { Fail("The fixed dispatch fact capacity was exceeded."); return; }
        frame.Dispatches[frame.DispatchCount++] = new(_passDepth == 0 ? 0 : _passStack[_passDepth - 1] + 1, x, y, z, facts);
    }
    internal void TargetFacts(IGpuFramebuffer target)
    {
        if (_frame < 0) return;
        var frame = Current;
        var outputs = target.Outputs;
        if (frame.TargetCount == frame.Targets.Length || outputs.Colour.Length > 8)
        { Fail("The fixed target fact capacity was exceeded."); return; }
        int index = frame.TargetCount++;
        frame.Targets[index] = new(_passDepth == 0 ? 0 : _passStack[_passDepth - 1] + 1, target.Width, target.Height,
            outputs.SampleCount, outputs.Depth, outputs.Colour.Length);
        outputs.Colour.AsSpan().CopyTo(frame.TargetFormats.AsSpan(index * 8, 8));
    }
    internal void CopyBytes(long? bytes)
    {
        if (bytes is { } value) Count(RendererPerfCounter.CopyBytes, value);
        else Count(RendererPerfCounter.UnknownCopyBytes);
    }
    internal static int BytesPerPixel(GpuPixelFormat format) => format switch
    {
        GpuPixelFormat.R8UNorm => 1,
        GpuPixelFormat.R16G16B16A16Float or GpuPixelFormat.D32FloatS8UInt => 8,
        GpuPixelFormat.R8G8B8A8UNorm or GpuPixelFormat.B8G8R8A8UNorm or GpuPixelFormat.R32Float
            or GpuPixelFormat.D24UNormS8UInt or GpuPixelFormat.R16G16Float => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    internal RendererPerfFrameSample Harvest(int sampleId, GpuPerfTimestampResult? timing = null)
    {
        if (_frame >= 0) throw new InvalidOperationException("Harvest is outside every measured frame.");
        var frame = _frames[sampleId + 1];
        if (!frame.Ended) throw new InvalidOperationException("The frame has not ended.");
        bool validTiming = timing is { Valid: true } && timing.Ticks.Length == MarkerCount
            && timing.Period is { } period && double.IsFinite(period) && period > 0;
        var metrics = ImmutableDictionary.CreateBuilder<string, RendererPerfMetric>(StringComparer.Ordinal);
        for (int owner = 0; owner <= (_layout == RendererPerfLayout.PerPass ? _passes.Length : 0); owner++)
        {
            string key = owner == 0 ? "unattributed" : $"pass.{_passes[owner - 1].Id}.{_passes[owner - 1].Occurrence}";
            foreach (var counter in Enum.GetValues<RendererPerfCounter>())
                AddLogical(metrics, sampleId, owner, $"logical.{key}.{counter}", counter);
        }
        foreach (var counter in Enum.GetValues<RendererPerfCounter>())
            AddLogical(metrics, sampleId, -1, $"logical.total.{counter}", counter);
        metrics.Add("allocation.currentThread.bytes", frame.ThreadChanged ? Missing("bytes", "current-thread managed allocation", "The recording thread changed.")
            : Value(frame.ThreadBytes, "bytes", "current-thread managed allocation, exact delta, excludes native allocation"));
        metrics.Add("allocation.process.bytes", Value(frame.ProcessBytes, "bytes", "process managed allocation, approximate delta (precise=false), includes workers, overlaps current-thread"));
        for (int i = 0; i < 3; i++) metrics.Add($"gc.gen{i}.collections", Value(frame.GcDelta[i], "count", "process GC.CollectionCount delta"));
        metrics.Add("native.stateCalls", NativeDelta(frame.NativeStart, frame.NativeEnd, "count", "supplied native emitted state-call counter"));
        AddDeviceMetrics(metrics, frame);
        for (int i = 0; i < frame.DispatchCount; i++)
        {
            var dispatch = frame.Dispatches[i];
            string key = $"dispatch.{i}";
            metrics.Add($"{key}.owner", Value(dispatch.Owner, "owner-index", "zero is unattributed, otherwise pass index plus one"));
            metrics.Add($"{key}.groupX", Value(dispatch.X, "groups", "raw dispatch argument"));
            metrics.Add($"{key}.groupY", Value(dispatch.Y, "groups", "raw dispatch argument"));
            metrics.Add($"{key}.groupZ", Value(dispatch.Z, "groups", "raw dispatch argument"));
            uint[] local = [dispatch.Facts.LocalX, dispatch.Facts.LocalY, dispatch.Facts.LocalZ];
            for (int axis = 0; axis < 3; axis++) metrics.Add($"{key}.local{axis}", local[axis] == 0
                ? Missing("threads", "compiled compute shader local size", "The bound pipeline metadata is unavailable.")
                : Value(local[axis], "threads", "compiled compute shader local size"));
        }
        for (int i = 0; i < frame.TargetCount; i++)
        {
            var target = frame.Targets[i];
            string key = $"target.{i}";
            metrics.Add($"{key}.owner", Value(target.Owner, "owner-index", "zero is unattributed, otherwise pass index plus one"));
            metrics.Add($"{key}.width", Value(target.Width, "pixels", "requested framebuffer"));
            metrics.Add($"{key}.height", Value(target.Height, "pixels", "requested framebuffer"));
            metrics.Add($"{key}.samples", Value(target.Samples, "samples", "requested framebuffer"));
            for (int colour = 0; colour < target.Colours; colour++)
            {
                var format = frame.TargetFormats[i * 8 + colour];
                metrics.Add($"{key}.colour{colour}", Value((int)format, "format-id", format.ToString()));
            }
            metrics.Add($"{key}.depth", target.Depth is { } depth ? Value((int)depth, "format-id", depth.ToString())
                : new(null, "format-id", "requested framebuffer", RendererPerfMetricStatus.NotExecuted, "No depth attachment."));
        }
        var cpu = ImmutableArray.CreateBuilder<RendererPerfCpuSpan>(frame.CpuCount);
        for (int i = 0; i < frame.CpuCount; i++) cpu.Add(new(frame.Cpu[i].Id, frame.Cpu[i].Parent, frame.Cpu[i].Start, frame.Cpu[i].End));
        int passCount = _layout == RendererPerfLayout.PerPass ? _passes.Length : 0;
        var passes = ImmutableArray.CreateBuilder<RendererPerfPassSample>(passCount);
        for (int i = 0; i < passCount; i++)
        {
            var definition = _passes[i];
            ulong? ticks = validTiming && !definition.DeviceOnly && _layout == RendererPerfLayout.PerPass && frame.PassStates[i] == 1
                ? timing!.Ticks[(int)definition.EndMarker] - timing.Ticks[(int)definition.BeginMarker] : null;
            var duration = frame.PassStates[i] == 2 ? new(null, "ns", "reserved skipped occurrence", RendererPerfMetricStatus.NotExecuted, "The pass did not execute.")
                : ticks is { } elapsed ? Duration(elapsed, timing!.Period!.Value, "native GPU timestamp interval")
                : Missing("ns", "native GPU timestamps", definition.DeviceOnly ? "Device work has CPU ownership only, without a GPU interval."
                    : _layout == RendererPerfLayout.TotalOnly ? "Total-only layout has no pass timestamps." : "No valid pass timestamp result.");
            passes.Add(new(definition.Id, definition.Occurrence, definition.Parent < 0 ? null : _passes[definition.Parent].Id,
                definition.Parent < 0 ? null : _passes[definition.Parent].Occurrence, definition.BeginMarker, definition.EndMarker,
                ticks, duration, definition.DeviceOnly));
        }
        ulong? totalTicks = validTiming ? timing!.Ticks[^1] : null;
        return new(frame.FrameId, frame.SequenceIndex, frame.Start, frame.End, _frequency, totalTicks, timing?.Period,
            totalTicks is { } total ? Duration(total, timing!.Period!.Value, "native completed-frame outer timestamp pair")
                : Missing("ns", "native GPU timestamps", timing?.Reason ?? "No GPU timestamp result was supplied."),
            passes.MoveToImmutable(), cpu.MoveToImmutable(), metrics.ToImmutable(),
            frame.Error is { } error ? [error] : _timestamps?.Failure is { } ringError ? [ringError] : []);
    }

    void AddLogical(ImmutableDictionary<string, RendererPerfMetric>.Builder metrics, int sampleId, int owner, string key, RendererPerfCounter counter)
    {
        var unknown = counter switch
        {
            RendererPerfCounter.CopyBytes => RendererPerfCounter.UnknownCopyBytes,
            RendererPerfCounter.Triangles => RendererPerfCounter.UnknownTopologyDraws,
            RendererPerfCounter.DispatchInvocations => RendererPerfCounter.UnknownLocalSizeDispatches,
            _ => (RendererPerfCounter?)null
        };
        double value = ReadCounter(sampleId, counter, owner);
        if (unknown is { } missing && ReadCounter(sampleId, missing, owner) > 0)
        {
            metrics.Add(key, new(null, Unit(counter), "logical requested seam commands", RendererPerfMetricStatus.ModelIncomplete, "Some commands have no resource or pipeline metadata."));
            metrics.Add($"{key}.known", Value(value, Unit(counter), "known logical portion only"));
        }
        else metrics.Add(key, Value(value, Unit(counter), "logical requested seam commands"));
    }
    static RendererPerfMetric Duration(ulong ticks, double period, string source)
        => ticks == 0 ? new(null, "ns", source, RendererPerfMetricStatus.BelowResolution, "The interval is below the timer resolution.")
            : Value(ticks * period, "ns", source);
    static string Unit(RendererPerfCounter counter) => counter is RendererPerfCounter.BufferUploadBytes or RendererPerfCounter.TextureUploadBytes
        or RendererPerfCounter.RequestedBufferBytes or RendererPerfCounter.RequestedTextureBytes or RendererPerfCounter.CopyBytes
        or RendererPerfCounter.ResolveReadBytes or RendererPerfCounter.ResolveWriteBytes ? "bytes" : "count";
    static RendererPerfMetric Value(double value, string unit, string source) => new(value, unit, source, RendererPerfMetricStatus.Available, null);
    static RendererPerfMetric Missing(string unit, string source, string reason) => new(null, unit, source, RendererPerfMetricStatus.Unsupported, reason);
    static RendererPerfMetric NativeDelta(long? start, long? end, string unit, string source)
        => start is null || end is null ? Missing(unit, source, "The native counter is not supplied.")
            : end < start ? new(null, unit, source, RendererPerfMetricStatus.Invalid, "The cumulative native counter decreased.")
            : Value((end - start).Value, unit, source);
    static void AddDeviceMetrics(ImmutableDictionary<string, RendererPerfMetric>.Builder metrics, Frame frame)
    {
        var start = frame.DeviceStart; var end = frame.DeviceEnd;
        bool supported = start.HasValue && end.HasValue;
        void Add(string key, double a, double b, string unit)
            => metrics.Add(key, !supported ? Missing(unit, "GpuDeviceCounters delta", "The device has no native counters.")
                : b < a ? new(null, unit, "GpuDeviceCounters delta", RendererPerfMetricStatus.Invalid, "The cumulative device counter decreased.")
                : Value(b - a, unit, "GpuDeviceCounters delta"));
        Add("native.framesBegun", start.FramesBegun, end.FramesBegun, "count");
        Add("native.drains", start.DrainCount, end.DrainCount, "count");
        Add("native.drainMs", start.DrainMs, end.DrainMs, "ms");
        Add("native.backpressureStalls", start.BackpressureStallCount, end.BackpressureStallCount, "count");
        Add("native.backpressureMs", start.BackpressureStallMs, end.BackpressureStallMs, "ms");
        Add("native.offTimelineDeferred", start.OffTimelineDeferred, end.OffTimelineDeferred, "count");
        Add("native.acquireWaits", start.AcquireWaitCount, end.AcquireWaitCount, "count");
        Add("native.acquireWaitMs", start.AcquireWaitMs, end.AcquireWaitMs, "ms");
        metrics.Add("native.offTimelineOutstanding", supported ? Value(end.OffTimelineOutstanding, "count", "GpuDeviceCounters end-of-frame gauge")
            : Missing("count", "GpuDeviceCounters gauge", "The device has no native counters."));
    }

    void ValidateLayout()
    {
        var used = new bool[(_passes.Length - _devicePasses.Count) * 2 + 2];
        for (int i = 0; i < _passes.Length; i++)
        {
            var pass = _passes[i];
            if (string.IsNullOrWhiteSpace(pass.Id) || pass.Occurrence < 0 || pass.Parent < -1 || pass.Parent >= i
                || pass.Parent >= 0 && _passes[pass.Parent].DeviceOnly)
                throw new ArgumentException("Invalid frozen pass layout.");
            if (pass.DeviceOnly)
            {
                if (pass.Occurrence != 0 || pass.BeginMarker != 0 || pass.EndMarker != 0)
                    throw new ArgumentException("Device ownership cannot reserve GPU markers.");
            }
            else if (pass.BeginMarker < 1 || pass.EndMarker <= pass.BeginMarker || pass.EndMarker >= used.Length - 1
                || used[pass.BeginMarker] || used[pass.EndMarker]) throw new ArgumentException("Invalid frozen pass layout.");
            for (int j = 0; j < i; j++)
            {
                var other = _passes[j];
                if (pass.Id == other.Id && pass.Occurrence == other.Occurrence) throw new ArgumentException("Duplicate pass identity.");
                if (pass.DeviceOnly || other.DeviceOnly) continue;
                bool inside = pass.BeginMarker > other.BeginMarker && pass.EndMarker < other.EndMarker;
                bool overlap = pass.BeginMarker < other.EndMarker && pass.EndMarker > other.BeginMarker;
                if (overlap && !inside) throw new ArgumentException("Pass intervals cross.");
                if (inside && (pass.Parent < j || pass.Parent < 0)) throw new ArgumentException("Pass parent does not match its interval.");
            }
            if (!pass.DeviceOnly && pass.Parent >= 0 && !(pass.BeginMarker > _passes[pass.Parent].BeginMarker && pass.EndMarker < _passes[pass.Parent].EndMarker))
                throw new ArgumentException("Pass parent does not contain its interval.");
            if (!pass.DeviceOnly) used[pass.BeginMarker] = used[pass.EndMarker] = true;
        }
    }

    internal readonly record struct ResourceFacts(bool Known, uint Layers = 1, GpuPrimitiveTopology? Topology = null,
        uint LocalX = 0, uint LocalY = 0, uint LocalZ = 0, int? OccurrenceId = null);
    readonly record struct DispatchSample(int Owner, uint X, uint Y, uint Z, ResourceFacts Facts);
    readonly record struct TargetSample(int Owner, uint Width, uint Height, int Samples, GpuPixelFormat? Depth, int Colours);
    readonly record struct CpuSegment(string Id, string Parent, long Start, long End);
    sealed class Frame(int passCount, int cpuCapacity)
    {
        internal readonly long[] Counters = new long[(passCount + 1) * CounterNames.Length];
        internal readonly byte[] PassStates = new byte[passCount];
        internal readonly CpuSegment[] Cpu = new CpuSegment[cpuCapacity];
        internal readonly DispatchSample[] Dispatches = new DispatchSample[cpuCapacity];
        internal readonly TargetSample[] Targets = new TargetSample[cpuCapacity];
        internal readonly GpuPixelFormat[] TargetFormats = new GpuPixelFormat[cpuCapacity * 8];
        internal readonly int[] GcStart = new int[3], GcDelta = new int[3];
        internal bool Begun, Ended, ThreadChanged;
        internal int FrameId, SequenceIndex, CpuCount, ThreadId, DispatchCount, TargetCount;
        internal long Start, End, ThreadStart, ProcessStart, ThreadBytes, ProcessBytes;
        internal long? NativeStart, NativeEnd;
        internal GpuDeviceCounters DeviceStart, DeviceEnd;
        internal string? Error;
    }
}
