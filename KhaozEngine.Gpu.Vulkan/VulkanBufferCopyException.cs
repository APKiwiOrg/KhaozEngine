using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanBufferCopy refusal. Existing ArgumentOutOfRangeException catches remain valid.</summary>
public sealed class VulkanBufferCopyException : ArgumentOutOfRangeException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanBufferCopyException(VulkanBufferCopyRefusal reason, string? paramName, object? actualValue, string message)
        : base(paramName, actualValue, message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanBufferCopyRefusal Reason { get; }

    /// <summary>The side reported by the refusing validator, when relevant to the reason.</summary>
    public string? Side { get; init; }

    /// <summary>The size bytes reported by the refusing validator, when relevant to the reason.</summary>
    public uint? SizeBytes { get; init; }

    /// <summary>The offset bytes reported by the refusing validator, when relevant to the reason.</summary>
    public uint? OffsetBytes { get; init; }

    /// <summary>The buffer bytes reported by the refusing validator, when relevant to the reason.</summary>
    public uint? BufferBytes { get; init; }
}
