using System.Numerics;

namespace KhaozEngine.Windowing;

/// <summary>A camera-independent target for screen pan and anchored zoom.</summary>
public interface IPanZoomTarget
{
    /// <summary>Move displayed content in the positive direction of the supplied Pointer-space delta.</summary>
    void PanByScreenDelta(Vector2 delta);
    /// <summary>Enlarge content for factors above 1, keeping the material point under the anchor fixed.</summary>
    void ZoomByFactor(float factor, Vector2 anchor);
}
