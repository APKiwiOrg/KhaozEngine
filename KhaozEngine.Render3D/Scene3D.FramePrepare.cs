using KhaozEngine.Render3D.Rendering;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The frame's pre-recording phase: the half of <see cref="Scene3D"/> that runs the subsystems which have GPU
    /// work of their own to submit, at the one point in the frame where no command list is open.
    /// See <see cref="IFramePreparer"/> for why that point has to exist, and
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see> for what happens when it does not.
    /// </summary>
    public sealed partial class Scene3D
    {
        // Built on first use rather than in the constructor, so this phase costs a scene that never renders
        // nothing at all. One entry today, and the array is the registration, not a placeholder for generality.
        IFramePreparer[]? _preparers;

        // Whether this frame has already been prepared. Cleared by Begin, which is what makes it PER FRAME.
        bool _framePrepared;

        /// <summary>
        /// Run the frame's pre-recording work. Call it ONCE per frame, after every <c>Draw*</c> call for the frame
        /// and BEFORE opening the command list the scene is rendered into.
        /// <para>
        /// Some of what a frame needs cannot be recorded into the frame's list: the FFT ocean's priming pass is a
        /// compute dispatch whose output another dispatch reads in the same frame, and the GPU seam's only ordering
        /// for that pair is a submit plus a device wait (#311), which means a command list of its own. Opening one
        /// while the frame's list is recording is what corrupts the device on Direct3D11 in immediate-context mode
        /// (#423), so it happens here instead. A frame that queues no water does nothing at all here.
        /// </para>
        /// <para>
        /// The order is <see cref="Begin"/> -> queue the frame's draws -> <c>PrepareFrame</c> -> open the frame's
        /// command list -> render. Skipping it throws from the water pass rather than rendering a stale ocean.
        /// </para>
        /// <para>
        /// <b>Once per frame, and the second call is a no-op</b> rather than a second preparation.
        /// <see cref="Render3DSurface.Render"/> calls it, so a host that also calls it by hand would otherwise
        /// prepare twice, and a second preparation is not harmless: it advances the wave clock again with a zero
        /// delta and hands the frame a surface one frame behind, silently. The flag is cleared by
        /// <see cref="Begin"/>, so it is the FRAME that is prepared once, not the scene. Queueing a draw after
        /// preparing is the misuse this cannot absorb, and the water pass throws for it.
        /// </para>
        /// <para>
        /// The other side of that latch: recording CONSUMES the prepared frame, so a scene records once per
        /// <see cref="Begin"/>. A host that wants the same scene recorded twice (two viewports, a split screen)
        /// begins again and re-queues, rather than rendering a second time off one preparation.
        /// </para>
        /// </summary>
        public void PrepareFrame()
        {
            if (_framePrepared) return;
            _framePrepared = true;
            var frame = new FramePrepare(Post.Water, RelativeWaterPlanes(), EffectTimeSeconds);
            IFramePreparer[] preparers = _preparers ??= new IFramePreparer[] { _water };
            for (int i = 0; i < preparers.Length; i++) preparers[i].PrepareFrame(frame);
        }
    }
}
