using KhaozEngine.Gpu;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// A headless 3D capture and the backend of the device that produced its RGBA8 pixels.
    /// </summary>
    public readonly record struct Render3DCapture(
        byte[] Rgba,
        int Width,
        int Height,
        GpuBackendKind Backend);
}
