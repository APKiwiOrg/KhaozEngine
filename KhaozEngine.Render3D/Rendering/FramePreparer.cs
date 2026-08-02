using System;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// A scene subsystem that has GPU work of its OWN to submit for the frame, on a command list that is not the
    /// frame's. <see cref="Scene3D.PrepareFrame"/> runs every preparer once per frame, after the frame's queues are
    /// filled and BEFORE the host opens the frame's command list.
    /// <para>
    /// <b>Why the phase exists at all.</b> A command list that opens while another is recording is not a second
    /// list on every backend: with Direct3D11 in immediate-context mode (which is how
    /// <c>GpuDeviceContext</c> creates the device) a list IS the device's immediate context, and opening one calls
    /// <c>ClearState</c> on it. That wipes every binding the frame's list believes is still bound, the managed
    /// binding cache skips re-binding them because nothing in ITS model changed, and the draws that follow execute
    /// with no render target and no shaders until the device faults
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/423">#423</see>). So a producer that has to
    /// submit and drain its own list runs here, where no frame list is open, rather than mid-recording.
    /// </para>
    /// <para>
    /// <b>Why not record that work into the frame's list instead.</b> Because the work in question is a dispatch
    /// whose output another dispatch reads, and the GPU seam has no dispatch-to-dispatch barrier
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/311">#311</see>): the only ordering it can offer
    /// for that pair is a submit and a device wait, which cannot be recorded into a list that is still open.
    /// </para>
    /// </summary>
    internal interface IFramePreparer
    {
        /// <summary>Do this frame's pre-recording work. Called once per frame from
        /// <see cref="Scene3D.PrepareFrame"/>, with no frame command list open, so an implementation may create,
        /// submit and drain command lists of its own. Must be cheap (ideally free) on a frame that has nothing to
        /// prepare, since it runs whether or not anything is queued for it.</summary>
        void PrepareFrame(in FramePrepare frame);
    }

    /// <summary>
    /// What a preparer is told about the frame: the scene-wide look and clock, plus the queues a preparer acts on.
    /// A view over the scene's own state (the plane span is the scene's live queue, already reduced by the render
    /// origin), so it is a <c>ref struct</c> and must not outlive the call.
    /// <para>
    /// It names the water queue explicitly rather than hiding it behind an abstraction, because the ocean is the
    /// only producer with pre-frame work today and a generic bag nobody can read is worse than a short list that
    /// says what the frame actually is. Add a field when a second producer needs one.
    /// </para>
    /// </summary>
    internal readonly ref struct FramePrepare
    {
        internal FramePrepare(WaterSettings water, ReadOnlySpan<WaterPlane> waterPlanes, float timeSeconds)
        {
            Water = water;
            WaterPlanes = waterPlanes;
            TimeSeconds = timeSeconds;
        }

        /// <summary>The frame's scene-wide water look (<c>Scene3D.Post.Water</c>).</summary>
        public WaterSettings Water { get; }

        /// <summary>The water planes queued this frame, in the render frame (see <c>Scene3D.RenderOrigin</c>).
        /// Empty when the frame draws no water.</summary>
        public ReadOnlySpan<WaterPlane> WaterPlanes { get; }

        /// <summary>The frame's effect clock (<see cref="Scene3D.EffectTimeSeconds"/>), which is the same value the
        /// water pass is drawn with.</summary>
        public float TimeSeconds { get; }
    }
}
