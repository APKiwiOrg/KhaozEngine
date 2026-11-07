using System.Numerics;
using KhaozEngine.MapDoc;

namespace KhaozEngine.MapEdit;

internal static class MapRenderViewAdmission
{
    internal static Vector3 LookDirection(Vector3 eye, Vector3 target)
    {
        Vector3 d = target - eye;
        if (d.LengthSquared() < 1e-12f)
            throw new MapRenderArgumentException(MapDocumentRefusal.RenderLookDirectionZero,
                "render_view eye and target coincide, so the look direction is zero. Move the eye away from the target.")
            { Eye = eye, Target = target };
        return Vector3.Normalize(d);
    }
}
