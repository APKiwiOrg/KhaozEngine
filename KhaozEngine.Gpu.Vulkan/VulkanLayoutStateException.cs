using System;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured VulkanLayout refusal. Existing InvalidOperationException catches remain valid.</summary>
public sealed class VulkanLayoutStateException : InvalidOperationException
{
    /// <summary>Construct a refusal with its required reason and original diagnostic data.</summary>
    public VulkanLayoutStateException(VulkanLayoutRefusal reason, string message)
        : base(message) => Reason = reason;

    /// <summary>The rule that refused the operation.</summary>
    public VulkanLayoutRefusal Reason { get; }

    /// <summary>The image reported by the refusing validator, when relevant to the reason.</summary>
    public ulong? Image { get; init; }

    /// <summary>The covered range count reported by the refusing validator, when relevant to the reason.</summary>
    public int? CoveredRangeCount { get; init; }

    /// <summary>The base mip reported by the refusing validator, when relevant to the reason.</summary>
    public uint? BaseMip { get; init; }

    /// <summary>The mip count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? MipCount { get; init; }

    /// <summary>The base layer reported by the refusing validator, when relevant to the reason.</summary>
    public uint? BaseLayer { get; init; }

    /// <summary>The layer count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? LayerCount { get; init; }

    /// <summary>The requested base mip reported by the refusing validator, when relevant to the reason.</summary>
    public uint? RequestedBaseMip { get; init; }

    /// <summary>The requested mip count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? RequestedMipCount { get; init; }

    /// <summary>The requested base layer reported by the refusing validator, when relevant to the reason.</summary>
    public uint? RequestedBaseLayer { get; init; }

    /// <summary>The requested layer count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? RequestedLayerCount { get; init; }

    /// <summary>The tracked base mip reported by the refusing validator, when relevant to the reason.</summary>
    public uint? TrackedBaseMip { get; init; }

    /// <summary>The tracked mip count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? TrackedMipCount { get; init; }

    /// <summary>The tracked base layer reported by the refusing validator, when relevant to the reason.</summary>
    public uint? TrackedBaseLayer { get; init; }

    /// <summary>The tracked layer count reported by the refusing validator, when relevant to the reason.</summary>
    public uint? TrackedLayerCount { get; init; }

    /// <summary>The target layout reported by the refusing validator, when relevant to the reason.</summary>
    public int? TargetLayout { get; init; }

    /// <summary>The resting layout reported by the refusing validator, when relevant to the reason.</summary>
    public int? RestingLayout { get; init; }
}
