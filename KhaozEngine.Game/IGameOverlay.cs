using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// What an overlay receives once, when <see cref="GameApp.AddOverlay"/> attaches it: the window, the point-space
    /// UI viewport its <see cref="IGameOverlay.DrawUi"/> pass is begun in, and the 2D surface for textures and fonts.
    /// </summary>
    public sealed record GameOverlayContext(AppWindow Window, UiViewport Ui, Render2DSurface Surface);

    /// <summary>
    /// An engine overlay that sits above whatever screen the game shows (title, login, loading or gameplay), added
    /// with <see cref="GameApp.AddOverlay"/>. Each frame it reads the raw input before the game does and may hold
    /// that frame's input, so the game, its pointers and the diagnostics HUD see
    /// <see cref="InputState.WithoutInteraction"/>. It draws in its own point-space UI pass after
    /// <see cref="GameApp"/>'s <c>OnDrawUi</c> and before the HUD. Overlays update and draw in add order on the
    /// frame thread, and a render-suppressed frame skips both. An overlay that throws from <see cref="Update"/> or
    /// <see cref="DrawUi"/> is logged once and removed, and the game keeps running. When a hold ends, a mouse button
    /// or gamepad still down stays hidden from the game until its release, so the game never sees a press that began
    /// before or during the hold. An overlay is identified by reference. Call from the frame thread.
    /// </summary>
    public interface IGameOverlay
    {
        /// <summary>Called once, inside <see cref="GameApp.AddOverlay"/>. A throw here propagates to the caller.</summary>
        void Attach(GameOverlayContext context);

        /// <summary>
        /// Read this frame's raw input. Return true to hold it from the game for this frame. Every overlay updates
        /// with the raw input even after an earlier one holds. <paramref name="realDt"/> is the unscaled frame delta.
        /// </summary>
        bool Update(InputState input, float realDt);

        /// <summary>Draw above the game's UI. <paramref name="batch"/> is already begun in the UI viewport.</summary>
        void DrawUi(SpriteBatch batch);
    }
}
