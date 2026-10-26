using System;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Gpu.Metal.Internal;
using KhaozEngine.Tests.Gpu.Performance;
using KhaozEngine.Tests.Gpu.RendererPerf;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

public sealed partial class RendererPerfMetalDiagnosticsTests
{
    [Fact]
    public void Thread_width_is_a_limit_not_occupancy()
    {
        var live = new DeviceLiveness();
        var table = MetalShaderIndexTable.Build([], [], "empty");
        var evidence = MetalShaderEvidence.Capture(new(MetalShaderStage.Compute, "kernel_entry", "emitted MSL"), "abc");
        using var shader = new MetalComputeShader(live, default, table, 8, 4, 2, evidence);
        int reads = 0;
        using var pipeline = new MetalComputePipeline(live, shader, [], default,
            () => { reads++; return new(32, 768); });
        Assert.Equal(0, reads);
        var diagnostics = Assert.IsAssignableFrom<IGpuPipelineDiagnostics>(pipeline);
        var report = diagnostics.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Unsupported, report.Status);
        Assert.Null(report.Identity.ShaderArtifactSha256.Sha256);
        Assert.NotNull(report.Reason);
        var executable = Assert.Single(report.Executables);
        Assert.Equal("kernel_entry", executable.EntryPoint);
        Assert.Equal((8u, 4u, 2u), (executable.LocalSizeX, executable.LocalSizeY, executable.LocalSizeZ));
        Assert.Equal(32ul, executable.ExecutionWidth.Value!.UInt64);
        Assert.Equal(768ul, executable.MaximumThreadsPerGroup.Value!.UInt64);
        Assert.Equal(GpuDiagnosticProvenance.HardwareLimit, executable.ExecutionWidth.Provenance);
        Assert.Null(executable.AchievedOccupancy.Value);
        Assert.Null(executable.PhysicalRegisters.Value);
        Assert.Null(executable.SpillBytes.Value);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", report.Identity.SourceSha256.Sha256);
        Assert.Equal(64, report.Identity.PipelineSha256.Sha256!.Length);
        Assert.Contains("fastMath=1", report.Identity.CompilerFlags);
        Assert.Equal(1, reads);
        var witness = ((IGpuPipelineConstructionDiagnostics)pipeline).ConstructionIdentity;
        Assert.Same(witness, report.Identity);
        Assert.Same(witness, ((IGpuPipelineConstructionDiagnostics)pipeline).ConstructionIdentity);
        live.MarkDead();
        Assert.Same(witness, ((IGpuPipelineConstructionDiagnostics)pipeline).ConstructionIdentity);
        Assert.Equal(GpuDiagnosticStatus.Invalid, diagnostics.ReadCompilerReport().Status);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Empty_sources_are_invalid_and_never_gain_available_empty_byte_hashes()
    {
        var live = new DeviceLiveness();
        var evidence = MetalShaderEvidence.Capture(new(MetalShaderStage.Compute, "main", ""), "");
        using var shader = new MetalComputeShader(live, default, MetalShaderIndexTable.Build([], [], "empty"), 1, 1, 1, evidence);
        using var pipeline = new MetalComputePipeline(live, shader, [], default);
        var report = pipeline.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Status);
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Identity.SourceSha256.Status);
        Assert.Null(report.Identity.SourceSha256.Sha256);
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Identity.Stages[0].EmittedSourceSha256!.Status);
        Assert.Null(report.Identity.ShaderArtifactSha256.Sha256);
    }

    [Fact]
    public void Logical_ring_capacity_is_separate_from_residency()
    {
        Assert.True(typeof(IGpuResourceMemoryDiagnostics).IsAssignableFrom(typeof(MetalGpuDevice)));
        var memory = new MetalMemoryDiagnostics();
        using var ring = memory.TrackBuffer(new(48, GpuBufferUsage.UniformBuffer), 3);
        using var staging = memory.TrackTexture(new(3, 2, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.Staging, mipLevels: 2, arrayLayers: 2));
        var snapshot = memory.ReadMemorySnapshot();
        Assert.Equal(48ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        Assert.Equal(56ul, snapshot.LogicalTextureBytes.Value!.UInt64);
        Assert.Equal(824ul, snapshot.StagingCapacityBytes.Value!.UInt64);
        Assert.Equal(104ul, snapshot.PeakLogicalPayloadBytes.Value!.UInt64);
        Assert.Null(snapshot.ResidentBytes.Value);
        Assert.Null(snapshot.BudgetBytes.Value);
        Assert.Null(snapshot.CommittedBytes.Value);
        Assert.Contains("overlaps", snapshot.StagingCapacityBytes.Source);
    }

    [Fact]
    public void Hosted_no_timestamp_route_is_cpu_only()
    {
        var native = new MetalTimestampTestNative { CounterSet = false, Stage = false, Blit = false };
        var api = new MetalTimestampApi(native);
        var capabilities = MetalCapabilityRead.Assemble("hosted fake", 1, api.SupportsTimestampCounters);
        Assert.False(capabilities.SupportsTimestamps);
        Assert.Equal(0, native.Allocations);
        Assert.Equal(0, native.ClockReads);
        using var device = new GpuPerfTimestampTestDevice { Features = capabilities, Software = false };
        var identity = RendererPerfProtocolTests.CompleteReport().Identity;
        var refusal = Assert.Throws<RendererPerfProtocolException>(() => RendererPerfAdmission.Device(device,
            new(identity, true, true, null, System.Collections.Immutable.ImmutableDictionary<string, RendererPerfMetric>.Empty)));
        Assert.Contains("required timestamp and completion proof", refusal.Message);
        Assert.Empty(device.Pools);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Missing_counter_or_sample_buffer_refuses_gpu_time(bool counter, bool failSamples)
    {
        var native = new MetalTimestampTestNative { CounterSet = counter, FailSamples = failSamples };
        var api = new MetalTimestampApi(native);
        Assert.False(api.SupportsTimestampCounters);
        Assert.Throws<System.NotSupportedException>(() => api.CreatePool(2, true));
        Assert.Equal(0, native.ClockReads);
        var capability = api.ReadCounterMetadata().TimestampCapability!;
        Assert.Equal(GpuDiagnosticStatus.Unsupported, capability.Status);
        Assert.Null(capability.Value);
        Assert.NotNull(capability.Reason);
        if (failSamples) Assert.Contains("injected sample allocation refused", capability.Reason);
    }

    [Fact]
    public void Counter_metadata_enumerates_actual_sets_and_all_sampling_points_without_sampling()
    {
        var native = new MetalTimestampTestNative
        {
            CounterSets = [new("Timestamp", ["Timestamp"]), new("Vendor set", ["Cycles", "Invocations"])]
        };
        var api = new MetalTimestampApi(native);
        Assert.Equal(0, native.MetadataReads);
        var metadata = api.ReadCounterMetadata();
        Assert.Equal(GpuDiagnosticStatus.Available, metadata.Status);
        Assert.Equal(new[] { "Timestamp", "Vendor set" }, metadata.Sets.Select(x => x.Name));
        Assert.Equal(new[] { "Cycles", "Invocations" }, metadata.Sets[1].Counters);
        Assert.Equal(new uint[] { 0 }, metadata.SamplingPoints);
        Assert.Equal(new uint[] { 0, 4, 0, 1, 2, 3, 4 }, native.SamplingQueries);
        Assert.Equal(1, native.Allocations);
        Assert.Single(native.Released);
        Assert.Equal(0, native.ClockReads);
        Assert.Empty(native.Trace);
        native.MetadataFailure = new InvalidOperationException("enumeration failed");
        var failed = api.ReadCounterMetadata();
        Assert.Equal(GpuDiagnosticStatus.Invalid, failed.Status);
        Assert.Contains("enumeration failed", failed.Reason);
        Assert.True(api.SupportsTimestampCounters);
    }

    [Fact]
    public void Cold_report_retains_counter_names_and_refused_timing_without_changing_pipeline_configuration()
    {
        var native = new MetalTimestampTestNative { CounterSets = [new("Vendor set", ["Cycles"])], FailSamples = true };
        var api = new MetalTimestampApi(native);
        var live = new DeviceLiveness();
        using var shader = new MetalComputeShader(live, default, MetalShaderIndexTable.Build([], [], "empty"), 8, 1, 1,
            MetalShaderEvidence.Capture(new(MetalShaderStage.Compute, "entry", "msl"), "source"));
        using var pipeline = new MetalComputePipeline(live, shader, [], default, readCounters: api.ReadCounterMetadata);
        Assert.Equal(0, native.MetadataReads);
        var report = pipeline.ReadCompilerReport();
        var executable = Assert.Single(report.Executables);
        var advertised = Assert.Single(executable.Statistics.Where(s => s.Name == "Cycles"));
        Assert.True(advertised.Metric.Value!.Boolean);
        Assert.Contains("Vendor set", advertised.Description);
        Assert.Equal(GpuDiagnosticProvenance.HardwareLimit, advertised.Metric.Provenance);
        var timing = Assert.Single(executable.Statistics.Where(s => s.Name == "timestamp-counters-usable"));
        Assert.Null(timing.Metric.Value);
        Assert.Contains("injected sample allocation refused", timing.Metric.Reason);
        Assert.Contains("reflection=none", report.Identity.CompilerFlags);
        Assert.Contains("capture=none", report.Identity.CompilerFlags);
        Assert.Empty(native.Trace);
        Assert.Equal(0, native.ClockReads);
    }

    [Fact]
    public void Native_memory_snapshot_preserves_exact_allocation_and_unified_provenance()
    {
        var memory = new MetalMemoryDiagnostics();
        int reads = 0;
        var raw = new MemoryNative();
        var snapshot = memory.Snapshot(false, () => { reads++; return MetalNativeMemoryQuery.Read(raw); });
        Assert.Equal(9007199254740993ul, snapshot.CommittedBytes.Value!.UInt64);
        Assert.True(snapshot.UnifiedMemory);
        Assert.Null(snapshot.ResidentBytes.Value);
        Assert.Null(snapshot.BudgetBytes.Value);
        Assert.Equal(GpuDiagnosticProvenance.BackendAllocation, snapshot.CommittedBytes.Provenance);
        Assert.Contains("unified CPU/GPU memory", snapshot.CommittedBytes.Source);
        Assert.Equal(0ul, snapshot.LogicalBufferBytes.Value!.UInt64);
        var dead = memory.Snapshot(true, () => { reads++; throw new Exception("must not query dead native device"); });
        Assert.Equal(GpuDiagnosticStatus.Invalid, dead.CommittedBytes.Status);
        Assert.Equal(1, reads);
        var failed = memory.Snapshot(false, () => throw new InvalidOperationException("native allocation query failed"));
        Assert.Equal(GpuDiagnosticStatus.Invalid, failed.CommittedBytes.Status);
        Assert.Contains("native allocation query failed", failed.CommittedBytes.Reason);
        Assert.Equal(GpuDiagnosticStatus.Available, failed.LogicalTextureBytes.Status);
        raw.UnifiedAvailable = false;
        raw.AllocationAvailable = false;
        var missing = MetalNativeMemoryQuery.Read(raw);
        Assert.Null(missing.UnifiedMemory);
        Assert.Null(missing.AllocatedBytes.Value);
        Assert.Equal(GpuDiagnosticStatus.Unsupported, missing.AllocatedBytes.Status);
        raw.AllocationAvailable = true;
        raw.AllocationFailure = new InvalidOperationException("native memory snapshot failed");
        Assert.Equal(GpuDiagnosticStatus.Invalid, MetalNativeMemoryQuery.Read(raw).AllocatedBytes.Status);
        Assert.Contains("native memory snapshot failed", MetalNativeMemoryQuery.Read(raw).AllocatedBytes.Reason);
    }

    [Fact]
    public void Compute_query_failures_do_not_erase_identity_or_invent_available_limits()
    {
        var live = new DeviceLiveness();
        using var shader = new MetalComputeShader(live, default, MetalShaderIndexTable.Build([], [], "empty"), 8, 1, 1,
            MetalShaderEvidence.Capture(new(MetalShaderStage.Compute, "entry", "msl"), "source"));
        using var failed = new MetalComputePipeline(live, shader, [], default,
            () => throw new InvalidOperationException("native pipeline limit query failed"));
        var report = failed.ReadCompilerReport();
        Assert.Equal(GpuDiagnosticStatus.Invalid, report.Status);
        Assert.Contains("native pipeline limit query failed", report.Reason);
        Assert.Equal(64, report.Identity.PipelineSha256.Sha256!.Length);
        var executable = Assert.Single(report.Executables);
        Assert.Equal(GpuDiagnosticStatus.Invalid, executable.ExecutionWidth.Status);
        Assert.Null(executable.ExecutionWidth.Value);
        using var zero = new MetalComputePipeline(live, shader, [], default, () => new(0, 512));
        Assert.Equal(GpuDiagnosticStatus.Invalid, zero.ReadCompilerReport().Status);
        using var absent = new MetalComputePipeline(live, shader, [], default,
            () => throw new NotSupportedException("selector absent"));
        Assert.Equal(GpuDiagnosticStatus.Unsupported, absent.ReadCompilerReport().Status);
        Assert.Contains("selector absent", absent.ReadCompilerReport().Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Discovery_failure_retains_reason_and_never_admits_gpu_time(bool unsupported)
    {
        var native = new MetalTimestampTestNative
        {
            DiscoveryFailure = unsupported
            ? new NotSupportedException("missing native counter selector") : new InvalidOperationException("native discovery failed")
        };
        var api = new MetalTimestampApi(native);
        Assert.False(api.SupportsTimestampCounters);
        Assert.Equal(unsupported ? GpuDiagnosticStatus.Unsupported : GpuDiagnosticStatus.Invalid, api.CapabilityStatus);
        Assert.Contains(unsupported ? "missing native counter selector" : "native discovery failed", api.UnavailableReason);
        Assert.Throws<NotSupportedException>(() => api.CreatePool(2, true));
        Assert.Equal(0, native.Allocations);
    }

    [Fact]
    public void Bounded_counter_enumeration_refuses_oversized_and_malformed_native_data()
    {
        var reader = new CounterReader();
        var sets = MetalCounterEnumeration.Read(reader);
        Assert.Equal("Vendor", Assert.Single(sets).Name);
        Assert.Equal(new[] { "Cycles", "Invocations" }, sets[0].Counters);
        reader.Sets = 65;
        reader.Reads = 0;
        Assert.Throws<InvalidOperationException>(() => MetalCounterEnumeration.Read(reader));
        Assert.Equal(0, reader.Reads);
        reader.Sets = 1; reader.Counters = 257;
        Assert.Throws<InvalidOperationException>(() => MetalCounterEnumeration.Read(reader));
        Assert.Equal(0, reader.Reads);
        reader.Counters = 1; reader.Name = "";
        Assert.Throws<InvalidOperationException>(() => MetalCounterEnumeration.Read(reader));
        reader.Sets = 0;
        Assert.Empty(MetalCounterEnumeration.Read(reader));
    }

    sealed class CounterReader : IMetalCounterSetReader
    {
        internal nuint Sets = 1, Counters = 2;
        internal int Reads;
        internal string Name = "Vendor";
        public nuint SetCount => Sets;
        public string SetName(nuint set) => Name;
        public nuint CounterCount(nuint set) => Counters;
        public string CounterName(nuint set, nuint counter) { Reads++; return counter == 0 ? "Cycles" : "Invocations"; }
    }

    sealed class MemoryNative : IMetalDeviceMemoryNative
    {
        internal bool UnifiedAvailable = true, AllocationAvailable = true;
        internal Exception? AllocationFailure;
        public bool HasAllocationQuery => AllocationAvailable;
        public bool HasUnifiedQuery => UnifiedAvailable;
        public nuint CurrentAllocatedSize => AllocationFailure is null ? unchecked((nuint)9007199254740993ul) : throw AllocationFailure;
        public bool HasUnifiedMemory => true;
    }
}
