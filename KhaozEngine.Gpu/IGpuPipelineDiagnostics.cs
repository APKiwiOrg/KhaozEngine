using System.Collections.Immutable;

namespace KhaozEngine.Gpu;

/// <summary>Optional cold compiler diagnostics on a graphics or compute pipeline. No implementation is required
/// by <see cref="IGpuPipeline"/> or <see cref="IGpuComputePipeline"/>. Read outside measured frames.</summary>
public interface IGpuPipelineDiagnostics
{
    /// <summary>Reads compiler evidence for this pipeline's actual diagnostic configuration. Capture-enabled
    /// pipeline timings are not interchangeable with production pipeline timings.</summary>
    GpuPipelineCompilerReport ReadCompilerReport();
}

/// <summary>Whether the backend supplied a valid diagnostic value.</summary>
public enum GpuDiagnosticStatus
{
    /// <summary>A value was supplied by the stated source.</summary>
    Available,
    /// <summary>The field is unavailable. Its value is null and its reason explains why.</summary>
    Unsupported,
    /// <summary>The query or evidence is invalid. Its value is null and its reason preserves the failure.</summary>
    Invalid
}

/// <summary>Meaning of a diagnostic source. Intermediate compiler values and hardware limits are not
/// physical register counts, spills, residency or achieved occupancy.</summary>
public enum GpuDiagnosticProvenance
{
    /// <summary>No supported source exists.</summary>
    Unknown,
    /// <summary>A documented vendor compiler statistic.</summary>
    VendorCompiler,
    /// <summary>An intermediate-code statistic, such as DXBC temporary slots.</summary>
    IntermediateCode,
    /// <summary>A measured native runtime counter.</summary>
    RuntimeCounter,
    /// <summary>A device or pipeline limit, such as SIMD execution width.</summary>
    HardwareLimit,
    /// <summary>Requested resource payload or retained capacity, not a backing allocation.</summary>
    LogicalPayload,
    /// <summary>Committed backend allocations, counted once per backing allocation.</summary>
    BackendAllocation,
    /// <summary>A supported native resident-memory query.</summary>
    Residency,
    /// <summary>A supported native memory-budget query.</summary>
    Budget
}

/// <summary>The native statistic's scalar representation, preserved without conversion to double.</summary>
public enum GpuDiagnosticValueType
{
    /// <summary>Boolean native statistic.</summary>
    Boolean,
    /// <summary>Signed 64-bit native statistic.</summary>
    Int64,
    /// <summary>Unsigned 64-bit native statistic, including values above double's exact integer range.</summary>
    UInt64,
    /// <summary>Finite floating-point native statistic.</summary>
    Float64
}

/// <summary>A tagged scalar. Exactly the member selected by Type must be present. Unavailable metrics
/// carry a null Value rather than a scalar containing zero.</summary>
/// <param name="Type">Native scalar type.</param>
/// <param name="Boolean">Boolean value, only for Boolean.</param>
/// <param name="Int64">Signed value, only for Int64.</param>
/// <param name="UInt64">Unsigned value, only for UInt64.</param>
/// <param name="Float64">Finite floating-point value, only for Float64.</param>
public sealed record GpuDiagnosticValue(GpuDiagnosticValueType Type, bool? Boolean = null, long? Int64 = null,
    ulong? UInt64 = null, double? Float64 = null);

/// <summary>A typed optional diagnostic with explicit units, provenance and support. An unavailable or invalid
/// value is null with a reason. Available does not imply hardware measurement.</summary>
/// <param name="Value">Native scalar, or null when unsupported or invalid.</param>
/// <param name="Unit">Explicit unit, including count, bytes, ratio or slots.</param>
/// <param name="Source">Native API/statistic and documentation identifying its meaning.</param>
/// <param name="Status">Support or failure status.</param>
/// <param name="Reason">Missing-field or invalidity cause, required when Value is null.</param>
/// <param name="Provenance">Kind of evidence, independently of support status.</param>
public sealed record GpuDiagnosticMetric(GpuDiagnosticValue? Value, string Unit, string Source,
    GpuDiagnosticStatus Status, string? Reason, GpuDiagnosticProvenance Provenance);

/// <summary>One vendor statistic with its original name and description.</summary>
/// <param name="Name">Unmodified vendor statistic name.</param>
/// <param name="Description">Unmodified vendor statistic description.</param>
/// <param name="Metric">Typed raw value and provenance.</param>
public sealed record GpuPipelineStatistic(string Name, string Description, GpuDiagnosticMetric Metric);

/// <summary>One native executable or entry stage. Physical fields require documented physical evidence.
/// Compiler occupancy estimates remain raw statistics. Execution width and threadgroup sizes are limits.</summary>
/// <param name="Name">Native executable name.</param>
/// <param name="Stage">Shader stage.</param>
/// <param name="EntryPoint">Shader entry point.</param>
/// <param name="LocalSizeX">Resolved local size X, or zero for a non-compute stage.</param>
/// <param name="LocalSizeY">Resolved local size Y, or zero for a non-compute stage.</param>
/// <param name="LocalSizeZ">Resolved local size Z, or zero for a non-compute stage.</param>
/// <param name="Statistics">Raw native statistics, retaining vendor names and types.</param>
/// <param name="PhysicalRegisters">Physical registers, never intermediate-code temporary slots.</param>
/// <param name="SpillBytes">Documented physical spill bytes, never inferred from temporary slots.</param>
/// <param name="AchievedOccupancy">Achieved runtime occupancy, never a compiler estimate or hardware limit.</param>
/// <param name="ExecutionWidth">Supported execution width limit, not occupancy.</param>
/// <param name="MaximumThreadsPerGroup">Supported threadgroup size limit, not occupancy.</param>
public sealed record GpuPipelineExecutableReport(string Name, string Stage, string EntryPoint,
    uint LocalSizeX, uint LocalSizeY, uint LocalSizeZ, ImmutableArray<GpuPipelineStatistic> Statistics,
    GpuDiagnosticMetric PhysicalRegisters, GpuDiagnosticMetric SpillBytes, GpuDiagnosticMetric AchievedOccupancy,
    GpuDiagnosticMetric ExecutionWidth, GpuDiagnosticMetric MaximumThreadsPerGroup);

/// <summary>Cold compiler evidence and its detached construction witness.</summary>
public sealed record GpuPipelineCompilerReport(GpuPipelineConstructionIdentity Identity,
    GpuDiagnosticStatus Status, string? Reason, ImmutableArray<GpuPipelineExecutableReport> Executables);
