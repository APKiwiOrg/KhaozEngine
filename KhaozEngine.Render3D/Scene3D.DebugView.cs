using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>The debug view (docs/design/TEMPORAL-FOUNDATIONS-DESIGN-2026-09-24.md, section 5): the selection, which
    /// turns temporal rendering on, and the dispatch that draws the frame's view after the post chain. Each view's pass
    /// lives with the data it shows.</summary>
    public sealed partial class Scene3D
    {
        SceneDebugView _debugView;

        /// <summary>
        /// A development view that replaces the final image with a view of the temporal machinery.
        /// <see cref="SceneDebugView.None"/>, the default, renders normally. Any other value turns temporal rendering on
        /// for as long as it is set, which jitters the rasterised image by under half an internal pixel on each axis
        /// each frame. Not a player setting.
        /// <para>
        /// The selection counts per frame. A change takes effect at the frame's first render, including one made
        /// between <see cref="Begin"/> and that render. A change made after that render waits for the next frame, so
        /// a second render inside the same frame keeps the first render's temporal state.
        /// </para>
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="SceneDebugView"/>.</exception>
        public SceneDebugView DebugView
        {
            get => _debugView;
            set
            {
                if (value < SceneDebugView.None || value > SceneDebugView.Reactive)
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined SceneDebugView.");
                _debugView = value;
            }
        }

        // The MotionVectors pass, built when the view is drawn and retired by the first frame that does not draw it.
        MotionVectorsView? _motionVectorsView;

        /// <summary>Whether the MotionVectors pass has been built. For tests.</summary>
        internal bool MotionVectorsViewBuiltForTests => _motionVectorsView is not null;

        /// <summary>Draw the frame's debug view over <paramref name="target"/>, once per render right after the post
        /// chain. <see cref="SceneDebugView.None"/> draws nothing. The view is the one the frame's first render latched
        /// (<see cref="LatchFrameView"/>), so a selection made after that render waits for the next frame, as
        /// <see cref="DebugView"/> documents.</summary>
        void DrawDebugView(IGpuCommandList cl, IGpuFramebuffer target)
        {
            switch (_frameDebugView)
            {
                case SceneDebugView.MotionVectors:
                    // A latched view means a temporal frame, and every render of a temporal frame carries the motion
                    // target. A miss is a wiring error, so it says so instead of failing on a null.
                    IGpuTexture motion = _res.MotionTex ?? throw new InvalidOperationException(
                        "The MotionVectors view has no motion target: the frame that latched it is not temporal.");
                    (_motionVectorsView ??= new MotionVectorsView(_gd, _targetOutput)).Draw(cl, motion, target);
                    break;
                default:
                    RetireMotionVectorsView();
                    break;
            }
        }

        // Its resource set samples the motion target, which a later resize or a temporal-off frame frees, so a frame
        // that does not draw the view lets it go. The last frame's commands may still read it, so it goes to the
        // retire queue.
        void RetireMotionVectorsView()
        {
            if (_motionVectorsView is null) return;
            _retired.Retire(_motionVectorsView);
            _motionVectorsView = null;
        }

        void DisposeMotionVectorsView()
        {
            _motionVectorsView?.Dispose();
            _motionVectorsView = null;
        }
    }
}
