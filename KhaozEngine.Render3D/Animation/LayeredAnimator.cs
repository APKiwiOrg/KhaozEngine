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
    /// frame (the industry-default reference); rotation deltas compose multiplicatively.</para>
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

        public LayeredAnimator(Skeleton skeleton)
        {
            _skeleton = skeleton ?? throw new ArgumentNullException(nameof(skeleton));
            _composited = new JointPose[skeleton.NodeCount];
            _layerScratch = new JointPose[skeleton.NodeCount];
        }

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

        /// <summary>Advance every layer's playhead by <paramref name="dt"/> seconds (each at its own
        /// <see cref="AnimationLayer.Speed"/>). Steady-state allocation-free.</summary>
        public void Update(float dt)
        {
            for (int i = 0; i < _layers.Count; i++) _layers[i].Update(dt);
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
            // Base is the rest pose; the first Override layer at full weight overwrites it wholesale (byte-stable).
            for (int n = 0; n < nodes; n++) _composited[n] = _skeleton.RestLocal[n];

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
        //   rotation: delta = sample * inverse(reference); apply delta LEFT of the base, scaled by w via a shortest-
        //             arc slerp from identity toward the full delta, composed multiplicatively: result = partial * base.
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
                Rotation = Quaternion.Normalize(partialDelta * baseP.Rotation),
                Scale = baseP.Scale + scaleDelta * w,
            };
        }
    }
}
