using System;

namespace KhaozEngine.Gpu;

/// <summary>Why a resource factory cannot honor a requested shader arithmetic policy.</summary>
public enum GpuShaderPolicyRefusal
{
    /// <summary>The factory implements only default shader compilation.</summary>
    PreciseMathUnavailable,
    /// <summary>The Vulkan device lacks binary32 signed-zero, infinity and NaN preservation.</summary>
    FloatControlsUnavailable,
}

/// <summary>A refused shader policy. Stage identifies the requested precise stage.</summary>
public sealed class GpuShaderPolicyUnsupportedException : NotSupportedException
{
    public GpuShaderPolicyUnsupportedException(GpuShaderPolicyRefusal reason, GpuShaderStages stage)
        : base("The GPU factory cannot honor the requested shader arithmetic policy.")
    { Reason = reason; Stage = stage; }
    public GpuShaderPolicyRefusal Reason { get; }
    public GpuShaderStages Stage { get; }
}
