using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Read-only orthographic iso camera surface (headless; fakeable in tests/consumers).</summary>
    public interface IIsoCamera3D
    {
        Matrix4x4 View { get; }
        Matrix4x4 Projection { get; }
        Matrix4x4 ViewProjection { get; }
        Vector3 Eye { get; }
        Vector3 Forward { get; }
    }
}
