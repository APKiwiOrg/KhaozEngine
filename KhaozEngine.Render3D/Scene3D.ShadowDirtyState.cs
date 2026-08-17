using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>
    /// The state half of the shadow depth-pass dirty-skip: what the last RENDERED pass put on the cascade atlas,
    /// which every dirty input is compared against, and the one commit that advances it. The pure compares live
    /// beside the depth pass itself (<see cref="Scene3D.ShadowDepthPassDirty"/>,
    /// <see cref="Scene3D.ShadowCastersChanged"/>, <see cref="Scene3D.ShadowCascadeVpsChanged"/> in
    /// Scene3D.ShadowCasters.cs), so the comparisons stay pure and headless-testable while the state they read
    /// lives in one place.
    /// <para>
    /// The atlas is reused and NOT cleared on a skipped frame, so whatever the last rendered pass drew is still on
    /// it. That is why the reference is the last RENDERED pass rather than the last frame, and it is why skinned
    /// presence is kept here too (issue #23). A skinned caster dirties the pass for as long as it EXISTS, so
    /// nothing used to record that one had existed: the frame a character despawned (or was dropped by
    /// <c>ClassifySkinnedVisibility</c>) read every input as unchanged, skipped, and reused an atlas with the
    /// character's shadow baked into it. Under a frozen camera and a held sun nothing else could trip, so the
    /// ghost stayed on the ground until an unrelated event dirtied the pass.
    /// </para>
    /// </summary>
    public sealed partial class Scene3D
    {
        readonly Matrix4x4[] _lastCascadeCpuVps = new Matrix4x4[ShadowSettings.MaxCascades];   // last rendered pass's per-cascade CPU fit
        int _lastShadowCascadeCount;          // last rendered pass's cascade count
        int _lastShadowResolution;            // allocated per-cascade shadow-map resolution at the last rendered pass
        List<ShadowCasterSpan> _lastShadowCasterRuns = new();          // last pass's drawn caster spans (see Scene3D.ShadowCasters.cs)
        List<ShadowCasterInstance> _lastShadowCasterModels = new();    // last pass's caster world matrices + dissolves
        List<ShadowCasterSpan> _shadowCasterRunsScratch = new();       // this-frame scratch (swapped in on commit)
        List<ShadowCasterInstance> _shadowCasterModelsScratch = new();
        // Whether the last RENDERED pass drew any skinned caster, so the frame they all go away can tell that the
        // atlas it would otherwise reuse still holds their shadows. Only a commit writes it, and a commit also sets
        // _shadowPassRendered, so a true value already implies there is a previous pass to have baked them in.
        bool _lastAnySkinnedCaster;

        /// <summary>
        /// Whether this frame's skinned casters went from SOME to NONE since the last rendered pass, which is the
        /// one skinned transition the presence flag cannot express on its own (issue #23). True on exactly the
        /// frame the last skinned caster stops being drawn, so the pass re-renders once and lifts the vanished
        /// character's shadow off the atlas, and false again on every frame after it (the commit that ran for the
        /// re-render recorded no skinned casters), so a scene that is otherwise static goes straight back to
        /// skipping. The reverse transition needs nothing: a skinned caster that ARRIVES is present, and presence
        /// already forces a render.
        /// </summary>
        bool SkinnedCastersCleared(bool anySkinnedCaster) => !anySkinnedCaster && _lastAnySkinnedCaster;

        /// <summary>
        /// Make what the pass just rendered the reference the next frame is compared against. The caster signature
        /// buffers are SWAPPED rather than copied (the scratch now holds the just-rendered casters, so it becomes
        /// the kept copy and the old kept copy is reused as next frame's scratch), which is what keeps the per-frame
        /// check allocation-free. Called only from the dirty branch, so every field here describes a pass that
        /// really did put pixels in the atlas.
        /// </summary>
        void CommitShadowDirtyState(bool anySkinnedCaster)
        {
            (_lastShadowCasterRuns, _shadowCasterRunsScratch) = (_shadowCasterRunsScratch, _lastShadowCasterRuns);
            (_lastShadowCasterModels, _shadowCasterModelsScratch) = (_shadowCasterModelsScratch, _lastShadowCasterModels);
            Array.Copy(_cascadeCpuVps, _lastCascadeCpuVps, _cascadeCount);
            _lastShadowCascadeCount = _cascadeCount;
            _lastShadowResolution = _model.ShadowMap.Resolution;
            _lastAnySkinnedCaster = anySkinnedCaster;
            _shadowPassRendered = true;
        }
    }
}
