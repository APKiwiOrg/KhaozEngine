namespace KhaozEngine.Gpu.Metal;

/// <summary>The rules of the MetalRingLayout refusal family.</summary>
public enum MetalRingLayoutRefusal
{
    /// <summary>Refuses a uniform binding window that reaches outside its own Metal ring segment.</summary>
    BindWindowOutsideSegment,
    /// <summary>Refuses a Metal ring depth outside the validator supported range.</summary>
    FrameDepthOutsideRange,
}
