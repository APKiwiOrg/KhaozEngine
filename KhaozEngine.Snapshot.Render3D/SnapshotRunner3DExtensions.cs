using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Snapshot
{
    /// <summary>
    /// Adds the 3D shot to <see cref="SnapshotRunner"/>. In its own package (depends on Render3D) so a 2D-only
    /// game referencing <c>KhaozEngine.Snapshot</c> never pulls in the 3D renderer.
    /// </summary>
    public static class SnapshotRunner3DExtensions
    {
        /// <summary>
        /// Capture a 3D scene via <see cref="Render3DSnapshot.Capture(int,int,Action{Scene3D},Action{Scene3D},int)"/>
        /// (<paramref name="setup"/> runs once; <paramref name="drawFrame"/> per frame) and save it as
        /// <c>&lt;OutDir&gt;/&lt;name&gt;.png</c>; returns the written path.
        /// </summary>
        public static string Shot3D(this SnapshotRunner runner, string name, int width, int height,
            Action<Scene3D> setup, Action<Scene3D> drawFrame, int frames = 1)
        {
            if (runner is null) throw new ArgumentNullException(nameof(runner));
            byte[] rgba = Render3DSnapshot.Capture(width, height, setup, drawFrame, frames);
            return runner.Save(name, rgba, width, height);
        }
    }
}
