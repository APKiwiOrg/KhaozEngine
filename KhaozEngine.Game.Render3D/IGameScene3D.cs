using KhaozEngine.Render3D;

namespace KhaozEngine.Game
{
    /// <summary>
    /// A <see cref="GameScene"/> that also submits a 3D world pass. A scene implements this in addition to
    /// deriving from <see cref="GameScene"/>; the <c>SceneManager.Draw3D</c> extension dispatches to the visible
    /// scenes that implement it (the same visible set <see cref="SceneManager.Draw2D"/> uses). Kept out of
    /// <see cref="GameScene"/> itself so the base scene/loop framework has no 3D-renderer dependency.
    /// </summary>
    public interface IGameScene3D
    {
        /// <summary>Submit 3D instances; <paramref name="scene"/>'s <c>Begin()</c> is already called. Only invoked while the scene is visible.</summary>
        void OnDraw3D(Scene3D scene);
    }
}
