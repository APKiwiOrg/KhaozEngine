using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace KhaozEngine.Gpu.Vulkan;

/// <summary>A structured native backend creation refusal.</summary>
public sealed class VulkanBackendCreationStateException : InvalidOperationException
{
    readonly IReadOnlyList<string>? _candidateNames;
    readonly IReadOnlyList<string?>? _candidateRejections;

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

    /// <summary>An exception-owned read-only snapshot of candidate names at the refused creation operation.</summary>
    public IReadOnlyList<string>? CandidateNames
    {
        get => _candidateNames;
        init => _candidateNames = value is null ? null : ImmutableArray.CreateRange(value);
    }

    /// <summary>An exception-owned read-only snapshot of candidate rejections at the refused creation operation.</summary>
    public IReadOnlyList<string?>? CandidateRejections
    {
        get => _candidateRejections;
        init => _candidateRejections = value is null ? null : ImmutableArray.CreateRange(value);
    }
}
