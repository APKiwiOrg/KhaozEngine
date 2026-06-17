using KhaozEngine.Render3D;

namespace KhaozEngine.Game
{
    /// <summary>The 3D draw pass for a <see cref="SceneManager"/> (the bridge that keeps 3D out of the base framework).</summary>
    public static class SceneManager3DExtensions
    {
        /// <summary>
        /// Draw the visible scenes' 3D pass bottom-to-top - exactly the scenes <see cref="SceneManager.Draw2D"/>
        /// draws (from <see cref="SceneManager.FirstVisibleIndex"/> up), but only those implementing
        /// <see cref="IGameScene3D"/>. No-op on an empty stack. Call it inside the 3D scene's <c>Begin()</c>.
        /// </summary>
        public static void Draw3D(this SceneManager manager, Scene3D scene)
        {
            var scenes = manager.Scenes;
            for (int i = manager.FirstVisibleIndex(); i < scenes.Count; i++)
                if (scenes[i] is IGameScene3D s)
                    s.OnDraw3D(scene);
        }
    }
}
