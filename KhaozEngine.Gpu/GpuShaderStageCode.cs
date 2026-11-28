using System;

namespace KhaozEngine.Gpu;

/// <summary>One compiled shader stage in every backend's form.</summary>
public sealed class GpuShaderStageCode
{
    internal GpuShaderStageCode(string entryPoint, ReadOnlyMemory<byte> spirv, string msl, string hlsl)
    {
        EntryPoint = entryPoint;
        Spirv = spirv;
        Msl = msl;
        Hlsl = hlsl;
    }

    /// <summary>The entry point name in every form.</summary>
    public string EntryPoint { get; }

    /// <summary>The SPIR-V module bytes.</summary>
    public ReadOnlyMemory<byte> Spirv { get; }

    /// <summary>The Metal Shading Language source.</summary>
    public string Msl { get; }

    /// <summary>The HLSL source.</summary>
    public string Hlsl { get; }
}
