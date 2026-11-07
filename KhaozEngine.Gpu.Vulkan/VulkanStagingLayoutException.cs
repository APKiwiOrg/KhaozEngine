using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanStagingLayout refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class VulkanStagingLayoutException : ArgumentOutOfRangeException
{
    /// <summary>Construct a refusal with its required reason and original BCL diagnostic data.</summary>
    public VulkanStagingLayoutException(VulkanStagingLayoutRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanStagingLayoutRefusal Reason { get; }

    /// <summary>The source shape width.</summary>
    public uint Width { get; init; }

    /// <summary>The source shape height.</summary>
    public uint Height { get; init; }

    /// <summary>The source shape mip count.</summary>
    public uint MipLevels { get; init; }

    /// <summary>The source shape array layer count.</summary>
    public uint ArrayLayers { get; init; }

    /// <summary>The source shape pixel format.</summary>
    public GpuPixelFormat Format { get; init; }

    /// <summary>The layout quantity exceeding the mapping ceiling.</summary>
    public string? Quantity { get; init; }

    /// <summary>The computed size before narrowing.</summary>
    public ulong ComputedBytes { get; init; }

    /// <summary>The largest size the mapping contract can describe.</summary>
    public ulong MaximumBytes { get; init; }
}
