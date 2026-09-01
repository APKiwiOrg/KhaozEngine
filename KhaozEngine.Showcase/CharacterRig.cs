using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Game;
using KhaozEngine.Render3D;

namespace KhaozEngine.Showcase
{
    /// <summary>The outcome of one character-rig load. <see cref="Loaded"/> false means the room shows its greybox
    /// capsule instead, and <see cref="Message"/> is the developer console line saying why (dev diagnostics, not
    /// player-facing text, so it is deliberately outside the localization catalog).</summary>
    readonly struct CharacterRigLoad
    {
        CharacterRigLoad(bool loaded, SkinnedMeshHandle mesh, ReplicatedCharacterAnimators? animators, float scale, string message)
        {
            Loaded = loaded;
            Mesh = mesh;
            Animators = animators;
            Scale = scale;
            Message = message;
        }

        /// <summary>True when both <see cref="Mesh"/> and <see cref="Animators"/> are usable.</summary>
        public bool Loaded { get; }

        /// <summary>The uploaded skinned mesh, valid only when <see cref="Loaded"/> is true.</summary>
        public SkinnedMeshHandle Mesh { get; }

        /// <summary>The character bridge driving one brain per entity, non-null only when <see cref="Loaded"/> is true.</summary>
        public ReplicatedCharacterAnimators? Animators { get; }

        /// <summary>The capsule-match uniform scale baked into the bridge tuning.</summary>
        public float Scale { get; }

        /// <summary>One developer console line describing the outcome, always set.</summary>
        public string Message { get; }

        internal static CharacterRigLoad Ok(SkinnedMeshHandle mesh, ReplicatedCharacterAnimators animators, float scale, string message) =>
            new CharacterRigLoad(true, mesh, animators, scale, message);

        internal static CharacterRigLoad Failed(string message) =>
            new CharacterRigLoad(false, default, null, 1f, message);
    }

    /// <summary>
    /// The one character-rig load the 3D, dungeon and net rooms share: skinned-ingest the committed Quaternius
    /// Universal CC0 character plus its clips, map them onto the locomotion states, fit the model to the room's
    /// capsule, and build the canonical signal-driven bridge (<see cref="ReplicatedCharacterAnimators"/>). Each
    /// room used to carry its own copy, which drifted (see issue #189), so this is the single source now.
    /// </summary>
    static class CharacterRigLoader
    {
        /// <summary>The locomotion states mapped by glTF clip name. The two swim entries are absent from the
        /// shipped rig and degrade to Idle, which is why an incomplete match still loads.</summary>
        static readonly (LocomotionState State, string ClipName)[] ClipNames =
        {
            (LocomotionState.Idle, "Idle"),
            (LocomotionState.Walk, "Walk"),
            (LocomotionState.Run, "Run"),
            (LocomotionState.Jump, "Jump"),
            (LocomotionState.Fall, "Fall"),
            (LocomotionState.SwimIdle, "SwimIdle"),   // tread water (absent in this rig -> degrades to Idle)
            (LocomotionState.Swim, "Swim"),           // forward stroke (absent -> degrades to Idle)
        };

        /// <summary>The committed rig every room loads.</summary>
        public static string PlayerGlbPath => Path.Combine(AppContext.BaseDirectory, "assets", "character", "Player.glb");

        /// <summary>Load the rig at <paramref name="glbPath"/> into <paramref name="scene"/>, fitted to a capsule of
        /// half-height <paramref name="capsuleHalfHeight"/>. Never throws: a failure comes back as a
        /// <see cref="CharacterRigLoad"/> with <see cref="CharacterRigLoad.Loaded"/> false.</summary>
        public static CharacterRigLoad Load(Scene3D scene, string glbPath, float capsuleHalfHeight) => Load(
            () => GltfLoader.LoadSkinnedWithMaterial(glbPath),
            () => GltfLoader.LoadAnimations(glbPath),
            (mesh, maps) => scene.LoadSkinnedMesh(mesh, maps),
            handle => scene.UnloadSkinnedMesh(handle),
            capsuleHalfHeight);

        /// <summary>The load with its asset and GPU calls handed in, so the whole thing is exercisable with no
        /// device (see CharacterRigLoaderTests).</summary>
        internal static CharacterRigLoad Load(
            Func<(SkinnedGltfMesh Mesh, GltfMaterialMaps Maps)> loadMesh,
            Func<IReadOnlyList<AnimationClip>> loadClips,
            Func<SkinnedGltfMesh, GltfMaterialMaps, SkinnedMeshHandle> upload,
            Action<SkinnedMeshHandle> unload,
            float capsuleHalfHeight)
        {
            try
            {
                (SkinnedGltfMesh mesh, GltfMaterialMaps maps) = loadMesh();
                if (mesh.Skeleton is null) return CharacterRigLoad.Failed("Character has no skeleton, using the capsule.");
                SkinnedMeshHandle handle = upload(mesh, maps);

                Dictionary<LocomotionState, AnimationClip> clips = MapClips(loadClips());
                if (clips.Count == 0)
                {
                    unload(handle);
                    return CharacterRigLoad.Failed("Character has no expected clips, using the capsule.");
                }

                // Auto-fit the model to the capsule height (asset-agnostic) and bake that scale into the bridge tuning,
                // starting from CharacterAnimatorTuning.Default so every OTHER tunable (SlopeGlideRate,
                // SlopeGlideSnapDistance, StepSmoothingRate, YawSmoothing, ...) matches the reference adopter exactly.
                float modelHeight = ModelHeight(mesh);
                float scale = modelHeight > 0.01f ? (capsuleHalfHeight * 2f) / modelHeight : 1f;
                CharacterAnimatorTuning tuning = CharacterAnimatorTuning.Default;
                tuning.Scale = scale;
                tuning.Locomotion = new LocomotionThresholds(0.1f, 9f);   // matches the controller's 6/12 walk/run feel

                var animators = new ReplicatedCharacterAnimators(mesh.Skeleton, clips, tuning);
                return CharacterRigLoad.Ok(handle, animators, scale,
                    $"Animated character loaded ({mesh.BoneCount} bones, {clips.Count} clips, scale {scale:0.00}).");
            }
            catch (Exception e)
            {
                return CharacterRigLoad.Failed($"Character load failed ({e.Message}), falling back to the capsule.");
            }
        }

        /// <summary>The subset of <see cref="ClipNames"/> the asset actually carries, keyed by locomotion state.</summary>
        static Dictionary<LocomotionState, AnimationClip> MapClips(IReadOnlyList<AnimationClip> loaded)
        {
            var byName = new Dictionary<string, AnimationClip>();
            foreach (AnimationClip c in loaded) byName[c.Name] = c;
            var clips = new Dictionary<LocomotionState, AnimationClip>();
            foreach ((LocomotionState state, string name) in ClipNames)
                if (byName.TryGetValue(name, out AnimationClip? c)) clips[state] = c;
            return clips;
        }

        /// <summary>Model-space height (max - min Y) of the rest mesh, for the capsule-match scale. A mesh with no
        /// vertices reads as 0 rather than the negative infinity an unguarded max - min produces from the sentinel
        /// seeds.</summary>
        internal static float ModelHeight(SkinnedGltfMesh mesh)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (SkinnedVertex v in mesh.Vertices) { min = MathF.Min(min, v.Position.Y); max = MathF.Max(max, v.Position.Y); }
            return max > min ? max - min : 0f;
        }
    }
}
