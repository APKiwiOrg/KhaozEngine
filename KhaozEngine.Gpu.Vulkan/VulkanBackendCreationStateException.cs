using System;
using System.Collections.Generic;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured native backend creation refusal.</summary>
public sealed class VulkanBackendCreationStateException : InvalidOperationException
{
    /// <summary>Creates a refusal with its required reason.</summary>
    public VulkanBackendCreationStateException(VulkanBackendCreationRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused creation.</summary>
    public VulkanBackendCreationRefusal Reason { get; }

    /// <summary>AdapterKey captured at the refused native creation operation.</summary>
    public string? AdapterKey { get; init; }

    /// <summary>PhysicalDeviceCount captured at the refused native creation operation.</summary>
    public uint? PhysicalDeviceCount { get; init; }

    /// <summary>CandidateCount captured at the refused native creation operation.</summary>
    public int? CandidateCount { get; init; }

    /// <summary>CandidateNames captured at the refused native creation operation.</summary>
    public IReadOnlyList<string>? CandidateNames { get; init; }

    /// <summary>CandidateRejections captured at the refused native creation operation.</summary>
    public IReadOnlyList<string?>? CandidateRejections { get; init; }
}
