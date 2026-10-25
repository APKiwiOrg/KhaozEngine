using System.Collections.Generic;
using System.Collections.Immutable;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>The requested validation rung and the facts actually supplied at instance creation.</summary>
internal readonly record struct VulkanInstanceValidation(
    VulkanValidationMode RequestedMode,
    ImmutableArray<string> EnabledLayers,
    ImmutableArray<ValidationFeatureEnableEXT> EnabledFeatures)
{
    internal static VulkanInstanceValidation Select(VulkanValidationMode requested, IReadOnlyList<string> enabledLayers)
    {
        ImmutableArray<string> layers = [.. enabledLayers];
        ImmutableArray<ValidationFeatureEnableEXT> features = layers.Contains(VulkanValidation.LayerName)
            && VulkanValidation.WantsSynchronizationValidation(requested)
                ? [ValidationFeatureEnableEXT.SynchronizationValidationExt] : [];
        return new(requested, layers, features);
    }

    internal string DiagnosticConfiguration => $"validationRequested={RequestedMode},"
        + $"enabledInstanceLayers=[{string.Join(",", EnabledLayers)}],"
        + $"enabledValidationFeatures=[{string.Join(",", EnabledFeatures)}]";
}
