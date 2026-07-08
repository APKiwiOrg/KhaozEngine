using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Render3D
{
    /// <summary>Composites an ordered stack of <see cref="AnimationLayer"/>s into one final skeleton pose: a base
    /// layer at the bottom (locomotion), masked <see cref="LayerMode.Override"/> / <see cref="LayerMode.Additive"/>
    /// action layers above (attack-while-running). Layers are evaluated bottom-to-top; each composites into the
    /// running pose by its <c>weight x mask(node)</c>. Produces the joint-WORLD bone palette
    /// <see cref="Scene3D.DrawSkinned(SkinnedMeshHandle, ReadOnlySpan{Matrix4x4}, Matrix4x4, Primitives.Color)"/>
    /// consumes, exactly like <see cref="AnimationPlayer"/>.
    ///
    /// <para>BYTE-STABILITY: with zero layers this is the rest pose; with a single full-weight, unmasked
    /// <see cref="LayerMode.Override"/> layer the result is the layer's sampled+composed pose with no blend arithmetic
    /// applied (a direct copy, not a lerp toward it), so it is bit-identical to
    /// <c>AnimationSampler.SamplePose(clip, skel, time)</c> composed - the same path the single-clip player takes.</para>
    ///
    /// <para>Rotation blending matches the crossfade in <see cref="JointPose.Lerp"/>: shortest-arc
    /// <see cref="Quaternion.Slerp"/> (System.Numerics negates one input when their dot is negative, resolving the
    /// double cover) then re-normalize. Translation/scale lerp. Additive layers apply the clip's delta from its first
    /// frame (the industry-default reference); rotation deltas compose multiplicatively in the joint's LOCAL frame
    /// (<c>base * delta</c>, the Unity/Unreal/glTF-additive convention).</para>
    ///
    /// Presentation only; GPU-free; not thread-safe (one per character). Steady-state <see cref="Update"/> /
    /// <see cref="GetBonePalette"/> allocate nothing once the layer set and its buffers are established.</summary>
    public sealed class LayeredAnimator
    {
        readonly Skeleton _skeleton;
        readonly List<AnimationLayer> _layers = new();

        // Reused per-frame scratch (grown once, never per Update/GetBonePalette).
        JointPose[] _composited;   // the running composited pose (one per node)
        JointPose[] _layerScratch; // one layer's sampled pose

        // Optional externally-supplied base local poses (the locomotion crossfade). When set the stack composites over
        // these instead of the rest pose, so a game can drive the base with AnimationPlayer (keeping the crossfade and
        // byte-stability) and stack action layers on top. Null == compose from the rest pose (Task-1 layer-stack usage).
        JointPose[]? _baseLocals;

        public LayeredAnimator(Skeleton skeleton)
        {
            _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            _composited = new JointPose[skeleton.NodeCount];
            _layerScratch = new JointPose[skeleton.NodeCount];
        }

        /// <summary>The skeleton this animator poses.</summary>
        public Skeleton Skeleton => _skeleton;

        /// <summary>The layer stack in composition order (index 0 is the base, composited first; higher layers on
        /// top). Add via <see cref="AddLayer(AnimationLayer)"/>; the list is exposed read-only for inspection.</summary>
        public IReadOnlyList<AnimationLayer> Layers => _layers;

        /// <summary>Number of layers currently in the stack.</summary>
        public int LayerCount => _layers.Count;

        /// <summary>Append <paramref name="layer"/> to the top of the stack (composited last, over everything below).
        /// Returns it for chaining.</summary>
        public AnimationLayer AddLayer(AnimationLayer layer)
        {
            if (layer is null) throw new ArgumentNullException(nameof(layer));
            _layers.Add(layer);
            return layer;
        }

        /// <summary>Convenience: build + append a layer from a clip (see <see cref="AddLayer(AnimationLayer)"/>).
        /// Returns the created layer.</summary>
        public AnimationLayer AddLayer(AnimationClip clip, LayerMode mode = LayerMode.Override, BoneMask? mask = null, float weight = 1f, float speed = 1f)
            => AddLayer(new AnimationLayer(clip, mode, mask, weight, speed));

        /// <summary>Remove <paramref name="layer"/> from the stack. Returns true if it was present.</summary>
        public bool RemoveLayer(AnimationLayer layer) => _layers.Remove(layer);

        /// <summary>Remove the layer at <paramref name="index"/>.</summary>
        public void RemoveLayerAt(int index) => _layers.RemoveAt(index);

        /// <summary>Set the BASE local poses the stack composites over (one per <see cref="Skeleton.NodeCount"/> node,
        /// in node order): the pose an <see cref="LayerMode.Override"/> layer lerps FROM and an
        /// <see cref="LayerMode.Additive"/> layer adds ONTO at every node the layers do not fully replace. Pass the
        /// locomotion crossfade (via <see cref="AnimationPlayer.GetLocalPoses"/>) each frame so a masked upper-body
        /// action stacks over running legs. The buffer is COPIED into the animator's own scratch, so the caller may
        /// reuse it. Clear it with <see cref="ClearBase"/> to compose from the rest pose again (the Task-1 default).
        /// Steady-state allocation-free (the copy target is grown once).</summary>
        public void SetBaseLocals(ReadOnlySpan<JointPose> baseLocals)
        {
            if (baseLocals.Length != _skeleton.NodeCount)
                throw new ArgumentException($"baseLocals length {baseLocals.Length} must equal node count {_skeleton.NodeCount}.", nameof(baseLocals));
            _baseLocals ??= new JointPose[_skeleton.NodeCount];
            baseLocals.CopyTo(_baseLocals);
        }

        /// <summary>Forget any base set by <see cref="SetBaseLocals"/> so the stack composites over the rest pose.</summary>
        public void ClearBase() => _baseLocals = null;

        /// <summary>Advance every layer's playhead by <paramref name="dt"/> seconds (each at its own
        /// <see cref="AnimationLayer.Speed"/>) and step any in-flight one-shot ACTIONS started with
        /// <see cref="PlayAction"/> (fade in/out, auto-retire). Steady-state allocation-free.</summary>
        public void Update(float dt)
        {
            for (int i = 0; i < _layers.Count; i++) _layers[i].Update(dt);
            UpdateActions(dt);
        }

        /// <summary>Write the composited joint-WORLD bone palette into <paramref name="outPalette"/> (length
        /// <see cref="Skeleton.BoneCount"/>). With no layers this is the rest pose. Steady-state allocation-free.</summary>
        public void GetBonePalette(Matrix4x4[] outPalette)
        {
            if (outPalette is null) throw new ArgumentNullException(nameof(outPalette));
            ComposeLocals();
            _skeleton.ComposeInto(_composited, outPalette);
        }

        /// <summary>Allocate + return the composited joint-WORLD bone palette. Prefer <see cref="GetBonePalette"/>
        /// with a reused buffer in the per-frame draw path.</summary>
        public Matrix4x4[] BonePalette()
        {
            var palette = new Matrix4x4[_skeleton.BoneCount];
            GetBonePalette(palette);
            return palette;
        }

        // Composite the whole stack into _composited (per-node local poses). Bottom starts from rest; each layer
        // composites in order by its weight x mask(node). Kept allocation-free: reuses _composited + _layerScratch.
        void ComposeLocals()
        {
            int nodes = _skeleton.NodeCount;
            // Base is the externally-supplied locomotion locals if set, else the rest pose. A full-weight unmasked
            // Override layer overwrites it wholesale (byte-stable); with no contributing layer the base passes through
            // unchanged, so a locomotion base with no active action composes bit-identical to AnimationPlayer.
            JointPose[] baseLocals = _baseLocals ?? _skeleton.RestLocal;
            for (int n = 0; n < nodes; n++) _composited[n] = baseLocals[n];

            for (int li = 0; li < _layers.Count; li++)
            {
                AnimationLayer layer = _layers[li];
                float layerWeight = layer.Weight;
                if (layerWeight <= 0f) continue;   // zero-weight layer contributes nothing (fast-path skip)

                layer.SampleInto(_skeleton, _layerScratch);

                if (layer.Mode == LayerMode.Override)
                    CompositeOverride(nodes, layer, layerWeight);
                else
                    CompositeAdditive(nodes, layer, layerWeight);
            }
        }

        void CompositeOverride(int nodes, AnimationLayer layer, float layerWeight)
        {
            BoneMask? mask = layer.Mask;
            for (int n = 0; n < nodes; n++)
            {
                float w = mask is null ? layerWeight : layerWeight * mask.Weight(n);
                if (w <= 0f) continue;                       // node not touched: keep the base
                if (w >= 1f) { _composited[n] = _layerScratch[n]; continue; }   // full: direct copy (bit-identical single-full-layer path)
                _composited[n] = JointPose.Lerp(_composited[n], _layerScratch[n], w);
            }
        }

        void CompositeAdditive(int nodes, AnimationLayer layer, float layerWeight)
        {
            BoneMask? mask = layer.Mask;
            JointPose[] reference = layer.ReferencePose(_skeleton);
            for (int n = 0; n < nodes; n++)
            {
                float w = mask is null ? layerWeight : layerWeight * mask.Weight(n);
                if (w <= 0f) continue;
                _composited[n] = ApplyAdditive(_composited[n], _layerScratch[n], reference[n], w);
            }
        }

        // Additive composition of one node: contribute (sample - reference), scaled by w, on top of baseP.
        //   translation/scale: baseP + (sample - reference) * w   (a scale OFFSET, keeping unit scale a no-op)
        //   rotation: delta = sample * inverse(reference); apply the delta in the joint's LOCAL frame (RIGHT of the
        //             base), scaled by w via a shortest-arc slerp from identity toward the full delta, composed
        //             multiplicatively: result = base * partial. This is the Unity/Unreal/glTF-additive convention -
        //             an additive clip is authored as a per-joint delta in the joint's OWN local space, so an aim
        //             offset or attack layered over locomotion bends the joint relative to its current local pose
        //             (rather than swinging it around the parent axis, which base * delta and delta * base disagree on
        //             grossly for non-commuting rotations).
        // w == 0 leaves baseP unchanged (delta -> identity); w == 1 applies the full delta.
        static JointPose ApplyAdditive(in JointPose baseP, in JointPose sample, in JointPose reference, float w)
        {
            Vector3 transDelta = sample.Translation - reference.Translation;
            Vector3 scaleDelta = sample.Scale - reference.Scale;

            Quaternion fullDelta = Quaternion.Normalize(sample.Rotation * Quaternion.Inverse(reference.Rotation));
            // Scale the delta by w: shortest-arc slerp from identity toward fullDelta. Slerp handles the double cover
            // (negates fullDelta when Identity.fullDelta dot < 0, i.e. w scales the SHORT way around).
            Quaternion partialDelta = w >= 1f
                ? fullDelta
                : Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, fullDelta, w));

            return new JointPose
            {
                Translation = baseP.Translation + transDelta * w,
                Rotation = Quaternion.Normalize(baseP.Rotation * partialDelta),
                Scale = baseP.Scale + scaleDelta * w,
            };
        }

        // ---- one-shot actions ----

        enum ActionPhase { Idle, FadeIn, Sustain, FadeOut }

        // Lifecycle record for one pooled action slot. The slot's AnimationLayer lives in _layers at LayerIndex; a slot
        // is REUSED (its layer's clip/mask/weight reset) rather than removed, so N sequential actions churn no
        // allocation. Generation increments on each retire so a stale ActionHandle cannot drive a reused slot.
        sealed class ActionSlot
        {
            public AnimationLayer Layer = null!;
            public int LayerIndex;           // its index in _layers (fixed once the slot is created)
            public int Generation;           // bumped on retire; an ActionHandle carries the generation it was issued at
            public ActionPhase Phase = ActionPhase.Idle;
            public float Elapsed;            // real (wall-clock) seconds since PlayAction
            public float PlayDuration;       // real seconds for the clip to play once (clipDuration / speed)
            public float FadeIn;
            public float FadeOut;
            public float FadeOutStart;       // real-second mark the auto fade-out begins (PlayDuration - FadeOut)
            public bool Cancelling;          // Cancel() forced an early fade-out from the current weight
            public float CancelFromWeight;   // weight at the moment Cancel() was called (fade linearly from here)
            public float CancelElapsed;      // seconds into the cancel fade
            public float CancelDuration;     // the cancel fade-out length
        }

        readonly List<ActionSlot> _actionSlots = new();
        int _activeActions;   // slots not Idle, so an all-idle animator skips the action step entirely

        /// <summary>True while at least one one-shot action started by <see cref="PlayAction"/> is fading in, playing,
        /// or fading out. When false the stack is just its base + persistent layers (a game can then take the byte-stable
        /// base-only path).</summary>
        public bool HasActiveActions => _activeActions > 0;

        /// <summary>Play <paramref name="clip"/> ONCE as a one-shot action layered over the base (and any persistent
        /// layers): fade in over <paramref name="fadeIn"/> seconds, play the clip through, fade out over
        /// <paramref name="fadeOut"/> seconds ending as the clip finishes (the fade-out OVERLAPS the clip tail), then
        /// auto-retire and free the layer slot for reuse. <paramref name="mask"/> gates it spatially (e.g. an upper-body
        /// subtree so an attack drives the arms while the legs stay on locomotion); null == the whole skeleton.
        /// <paramref name="mode"/> selects Override (default, replace the masked bones) or Additive (add the clip's
        /// delta). <paramref name="speed"/> scales the clip playhead (the real play duration is
        /// <c>clip.Duration / speed</c>). Returns an <see cref="ActionHandle"/> for <see cref="Cancel"/>. Reuses a
        /// pooled slot when one is free, so repeated actions allocate nothing in steady state.</summary>
        public ActionHandle PlayAction(AnimationClip clip, BoneMask? mask = null, float fadeIn = 0.1f, float fadeOut = 0.1f,
            float speed = 1f, LayerMode mode = LayerMode.Override)
        {
            if (clip is null) throw new ArgumentNullException(nameof(clip));

            ActionSlot slot = AcquireSlot();
            AnimationLayer layer = slot.Layer;
            layer.SetClip(clip);        // resets Time to 0 and clears the additive reference for the reused slot
            layer.Mask = mask;
            layer.Mode = mode;
            layer.Speed = speed;
            layer.Weight = 0f;          // fade in from silent (no pop at start)

            float playSpeed = speed <= 0f ? 1f : speed;
            float playDuration = clip.Duration <= 0f ? 0f : clip.Duration / playSpeed;
            float fIn = MathF.Max(0f, fadeIn);
            float fOut = MathF.Max(0f, fadeOut);
            // The fade-out overlaps the clip tail: it must end at playDuration, so it starts fOut seconds before then.
            // Clamp so a fade-out longer than the clip (or overlapping the fade-in) still starts >= 0 and after fade-in.
            float fadeOutStart = MathF.Max(fIn, playDuration - fOut);

            slot.Phase = fIn > 0f ? ActionPhase.FadeIn : ActionPhase.Sustain;
            if (fIn <= 0f) layer.Weight = 1f;   // instant fade-in -> start at full
            slot.Elapsed = 0f;
            slot.PlayDuration = playDuration;
            slot.FadeIn = fIn;
            slot.FadeOut = fOut;
            slot.FadeOutStart = fadeOutStart;
            slot.Cancelling = false;
            _activeActions++;

            return new ActionHandle(this, slot.LayerIndex, slot.Generation);
        }

        /// <summary>Cancel an in-flight action early: fade it out cleanly from its CURRENT weight over its fade-out
        /// duration (no pose pop), then retire it. A no-op if the handle is stale (the action already retired or its
        /// slot was reused). Returns true if the handle referred to a live action.</summary>
        public bool Cancel(ActionHandle handle)
        {
            if (!TryResolve(handle, out ActionSlot? slot) || slot!.Phase == ActionPhase.Idle) return false;
            if (slot.Cancelling) return true;   // already cancelling
            slot.Cancelling = true;
            slot.CancelFromWeight = slot.Layer.Weight;   // fade from where we are now, not from 1 (continuity)
            slot.CancelElapsed = 0f;
            slot.CancelDuration = slot.FadeOut;          // reuse the action's fade-out length
            slot.Phase = ActionPhase.FadeOut;
            if (slot.CancelDuration <= 0f) RetireSlot(slot);   // instant cancel
            return true;
        }

        // Advance every non-idle action one frame: ramp its weight per phase, transition phases, retire when done.
        void UpdateActions(float dt)
        {
            if (_activeActions == 0) return;
            for (int i = 0; i < _actionSlots.Count; i++)
            {
                ActionSlot slot = _actionSlots[i];
                if (slot.Phase == ActionPhase.Idle) continue;

                if (slot.Cancelling)
                {
                    slot.CancelElapsed += dt;
                    float f = slot.CancelDuration <= 0f ? 1f : Math.Clamp(slot.CancelElapsed / slot.CancelDuration, 0f, 1f);
                    slot.Layer.Weight = slot.CancelFromWeight * (1f - f);
                    if (f >= 1f) RetireSlot(slot);
                    continue;
                }

                slot.Elapsed += dt;
                float t = slot.Elapsed;

                if (slot.Phase == ActionPhase.FadeIn)
                {
                    float w = slot.FadeIn <= 0f ? 1f : Math.Clamp(t / slot.FadeIn, 0f, 1f);
                    slot.Layer.Weight = w;
                    if (t >= slot.FadeIn) slot.Phase = ActionPhase.Sustain;
                }

                if (slot.Phase == ActionPhase.Sustain)
                {
                    slot.Layer.Weight = 1f;
                    if (t >= slot.FadeOutStart) slot.Phase = ActionPhase.FadeOut;
                }

                if (slot.Phase == ActionPhase.FadeOut && !slot.Cancelling)
                {
                    // Auto fade-out overlapping the tail: ramp 1 -> 0 across [FadeOutStart, PlayDuration].
                    float span = slot.PlayDuration - slot.FadeOutStart;
                    float w = span <= 0f ? 0f : Math.Clamp(1f - (t - slot.FadeOutStart) / span, 0f, 1f);
                    slot.Layer.Weight = w;
                    if (t >= slot.PlayDuration) RetireSlot(slot);
                }
            }
        }

        // Reuse an Idle slot if one exists, else create a new one (its layer appended to the stack once, then reused).
        ActionSlot AcquireSlot()
        {
            for (int i = 0; i < _actionSlots.Count; i++)
                if (_actionSlots[i].Phase == ActionPhase.Idle) return _actionSlots[i];

            // No free slot: append a fresh one. Its layer joins the stack at the top (composited over the base + any
            // earlier action layers) and STAYS there at weight 0 when idle (the w <= 0 fast-path skips it).
            var layer = new AnimationLayer(_placeholderClip(), LayerMode.Override, mask: null, weight: 0f);
            int layerIndex = _layers.Count;
            _layers.Add(layer);
            var slot = new ActionSlot { Layer = layer, LayerIndex = layerIndex, Generation = 0, Phase = ActionPhase.Idle };
            _actionSlots.Add(slot);
            return slot;
        }

        // Retire a slot: park its layer at weight 0 (skipped by compositing), bump the generation so outstanding
        // handles go stale, and mark it Idle for reuse. The layer stays in _layers (no list churn), so the next
        // PlayAction on a reused slot allocates nothing.
        void RetireSlot(ActionSlot slot)
        {
            slot.Layer.Weight = 0f;
            slot.Phase = ActionPhase.Idle;
            slot.Cancelling = false;
            slot.Generation++;
            _activeActions--;
        }

        bool TryResolve(ActionHandle handle, out ActionSlot? slot)
        {
            slot = null;
            if (!ReferenceEquals(handle.Owner, this)) return false;
            for (int i = 0; i < _actionSlots.Count; i++)
            {
                ActionSlot s = _actionSlots[i];
                if (s.LayerIndex == handle.SlotIndex)
                {
                    if (s.Generation != handle.Generation) return false;   // stale: slot was reused
                    slot = s;
                    return true;
                }
            }
            return false;
        }

        // A tiny zero-duration placeholder clip a freshly-pooled slot holds until its first PlayAction sets the real
        // one. Never sampled while the slot is idle (weight 0 skips it). Shared across all slots of this animator.
        AnimationClip? _placeholder;
        AnimationClip _placeholderClip() => _placeholder ??= new AnimationClip("__idle_action_slot__", 0f, new List<JointTrack>());
    }

    /// <summary>An opaque handle to a one-shot action started by <see cref="LayeredAnimator.PlayAction"/>, passed to
    /// <see cref="LayeredAnimator.Cancel"/> to fade it out early. Carries the owning animator, the pooled slot index,
    /// and the generation the slot was at when issued, so a handle to an action that has already retired (and whose
    /// slot may have been reused by a later action) resolves as stale and is safely ignored. A value type: copy it
    /// freely.</summary>
    public readonly struct ActionHandle
    {
        internal ActionHandle(LayeredAnimator owner, int slotIndex, int generation)
        {
            Owner = owner;
            SlotIndex = slotIndex;
            Generation = generation;
        }

        internal LayeredAnimator? Owner { get; }
        internal int SlotIndex { get; }
        internal int Generation { get; }

        /// <summary>True for a handle that was actually issued by a <see cref="LayeredAnimator.PlayAction"/> call (not
        /// a defaulted <c>default(ActionHandle)</c>). Does not imply the action is still live - a retired action's
        /// handle is still valid-shaped but resolves as stale in <see cref="LayeredAnimator.Cancel"/>.</summary>
        public bool IsValid => Owner is not null;
    }
}
