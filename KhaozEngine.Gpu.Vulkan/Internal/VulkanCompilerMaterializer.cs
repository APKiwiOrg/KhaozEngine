using System;
using System.Text;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal;

/// <summary>Materialization boundary. The reader admits counts and validates native UTF-8 before entering it.</summary>
internal interface IVulkanCompilerMaterializer
{
    PipelineExecutablePropertiesKHR[] Properties(int count);
    PipelineExecutableStatisticKHR[] Statistics(int count);
    string Text(ReadOnlySpan<byte> bytes);
}

internal sealed class VulkanCompilerMaterializer : IVulkanCompilerMaterializer
{
    internal static readonly VulkanCompilerMaterializer Instance = new();
    public PipelineExecutablePropertiesKHR[] Properties(int count) => new PipelineExecutablePropertiesKHR[count];
    public PipelineExecutableStatisticKHR[] Statistics(int count) => new PipelineExecutableStatisticKHR[count];
    public string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
