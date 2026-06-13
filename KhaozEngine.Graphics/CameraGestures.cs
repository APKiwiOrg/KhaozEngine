using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.Graphics;

/// <summary>Shared input-to-camera gesture helpers used by both <see cref="CameraController"/> and
/// <c>PannableCanvas</c>, so the press-origin tap math has a single implementation.</summary>
public static class CameraGestures
{
    /// <summary>
    /// True on the frame <paramref name="viewport"/> is tapped (press-origin and release both inside it,
    /// the click-through-safe invariant). Returns the press and release points in world coordinates via
    /// <paramref name="camera"/> so the caller can hit-test both and require the same target. A pan also
    /// satisfies the invariant, but the camera moved between press and release, so its press/release
    /// world points differ and the same-target check rejects it.
    /// </summary>
    public static bool TryGetTap(InputManager input, Camera2D camera, Viewport viewport,
                                 out Vector2 pressWorld, out Vector2 releaseWorld)
    {
        if (input.IsTapIn(viewport.Bounds))
        {
            pressWorld = camera.ScreenToWorld(input.PressOrigin, viewport);
            releaseWorld = camera.ScreenToWorld(input.PointerPosition, viewport);
            return true;
        }
        pressWorld = releaseWorld = Vector2.Zero;
        return false;
    }
}
