namespace KhaozEngine.Gpu;

/// <summary>Why GpuRgbaReadback refused an operation.</summary>
public enum GpuRgbaReadbackRefusal
{
    /// <summary>Refuses a whole-source RGBA8 readback incompatible with its requested staging shape.</summary>
    SourceShapeMismatch,
}
