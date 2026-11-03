namespace KhaozEngine.Gpu;

/// <summary>Why GpuPipelineOutputs refused an operation.</summary>
public enum GpuPipelineOutputsRefusal
{
    /// <summary>Refuses a graphics pipeline whose blend state count differs from its colour outputs.</summary>
    BlendCountMismatch,
}
