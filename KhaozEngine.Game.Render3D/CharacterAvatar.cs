using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Game
{
    /// <summary>
    /// Turnkey third-person animated character: the one object a game constructs to get movement + climbing +
    /// collision-robust facing + locomotion animation + drawing, with no per-game glue. It composes the three engine
    /// pieces that already exist - <see cref="CharacterController3D"/> (the body: walking, slopes, smooth stair
    /// climbing, collision), <see cref="AnimatedCharacter"/> (the brain: idle/walk/run/jump/fall/swim clip selection),
    /// and <see cref="CharacterFacing"/> (which way to face) - and wires them the way every game was re-wiring them by
    /// hand: face the intended move direction (so a scraped wall never spins the model), feed the animator the REAL
    /// collision-clamped speed plus the controller's grounded/vertical/swim state, and draw the skinned mesh at the
    /// capsule's feet. Call <see cref="Update"/> once a frame then <see cref="Draw(Scene3D, Color)"/>.
    /// <para>
    /// The composed pieces stay usable on their own - a game that needs only movement uses <see cref="CharacterController3D"/>
    /// directly, one that drives a remote player's animation from replicated state uses <see cref="AnimatedCharacter"/>
    /// directly, and the facing math is the static <see cref="CharacterFacing"/>. This bundle is the convenient default,
    /// never a requirement. Build it from an already-loaded rig via the constructor, or from a glTF asset via
    /// <see cref="TryLoadGltf"/> (the standard clip-name mapping + capsule-match scaling). Client-cosmetic: the pose and
    /// facing never feed simulation or netcode; drive the LOCAL player from input and a REMOTE player from its own
    /// controller/animator fed replicated state.
    /// </para>
    /// </summary>
    public sealed class CharacterAvatar
    {
        readonly CharacterController3D _controller;
        readonly AnimatedCharacter _animation;
        readonly SkinnedMeshHandle _mesh;
        readonly float _modelScale;
        float _facingYaw;
        float _renderY;   // presentation-smoothed draw height (eases the discrete stair step snaps; see RenderPosition)

        /// <summary>Default facing turn rate (radians/second): how fast the model rotates toward the intended move
        /// direction. 12 rad/s (~690 deg/s) turns a quarter-circle in about 0.13 s - responsive without snapping.</summary>
        public const float DefaultMaxTurnRate = 12f;

        /// <summary>Facing turn rate (radians/second) toward the intended move direction; see
        /// <see cref="CharacterFacing.TurnTowards"/>. Set &lt;= 0 to snap instantly. Default
        /// <see cref="DefaultMaxTurnRate"/>.</summary>
        public float MaxTurnRate = DefaultMaxTurnRate;

        /// <summary>Default draw-height smoothing rate (metres/second): see <see cref="RenderHeightSmoothRate"/>.
        /// 6 m/s eases a ~0.33 m stair riser over ~3-4 frames.</summary>
        public const float DefaultRenderHeightSmoothRate = 6f;

        /// <summary>Rate (metres/second) at which the DRAWN height (and <see cref="RenderPosition"/>, which a follow
        /// camera should target) eases toward the physics height, so the discrete height snaps of stair geometry - a
        /// descending stair drops a whole riser in one physics tick - read as a smooth glide instead of a bumpy jolt on
        /// the model and the camera. Only GROUNDED height changes are eased; while airborne (a jump or a real fall) the
        /// draw height snaps to physics so the arc stays crisp, and a teleport-sized jump (beyond
        /// <see cref="RenderHeightSnapDistance"/>) snaps rather than crawling. Horizontal position is never smoothed (no
        /// input lag). Set &lt;= 0 to disable (draw exactly at the physics height). Default
        /// <see cref="DefaultRenderHeightSmoothRate"/>.</summary>
        public float RenderHeightSmoothRate = DefaultRenderHeightSmoothRate;

        /// <summary>Height gap (metres) beyond which the draw height snaps to physics instead of easing - a respawn or
        /// teleport should not crawl. Default 1.5 (well above any single stair riser, below a floor-to-floor jump).</summary>
        public float RenderHeightSnapDistance = 1.5f;

        /// <summary>The composed movement body. Read its tuning fields (speeds, capsule size, step-climb) or its state
        /// directly; do not call its <see cref="CharacterController3D.Update"/> yourself while using the avatar (the
        /// avatar drives it once per frame).</summary>
        public CharacterController3D Controller => _controller;

        /// <summary>The composed animation brain. Use it for actions stacked over locomotion
        /// (<see cref="AnimatedCharacter.PlayAction"/>) or to read the current <see cref="AnimatedCharacter.State"/>;
        /// the avatar drives its locomotion <see cref="AnimatedCharacter.Update(float,bool,float,bool,float)"/> each frame.</summary>
        public AnimatedCharacter Animation => _animation;

        /// <summary>Current world position (capsule centre) from the controller - the crisp physics position (use this
        /// for gameplay/streaming/queries). For the follow camera's target and any presentation that should not bump on
        /// stairs, use <see cref="RenderPosition"/>.</summary>
        public Vector3 Position => _controller.Position;

        /// <summary>The presentation position the character is DRAWN at: the physics X/Z (never smoothed, so movement
        /// stays responsive) with the height eased toward the physics height at <see cref="RenderHeightSmoothRate"/>.
        /// Point a follow camera's target at this (not <see cref="Position"/>) so the camera glides up and down stairs
        /// instead of jolting on each discrete step.</summary>
        public Vector3 RenderPosition => new(_controller.Position.X, _renderY, _controller.Position.Z);

        /// <summary>True while grounded (from the controller).</summary>
        public bool Grounded => _controller.Grounded;

        /// <summary>Current vertical velocity, m/s positive up (from the controller).</summary>
        public float VerticalVelocity => _controller.VerticalVelocity;

        /// <summary>True while surface-swimming (from the controller; requires a fluid-medium provider).</summary>
        public bool Swimming => _controller.Swimming;

        /// <summary>Current facing yaw (radians), the heading the model is drawn at. Eased toward the intended move
        /// direction each <see cref="Update"/> at <see cref="MaxTurnRate"/>.</summary>
        public float FacingYaw => _facingYaw;

        /// <summary>Uniform model scale applied in <see cref="Draw(Scene3D, Color)"/> (a <see cref="TryLoadGltf"/> avatar scales the rig
        /// to the capsule height; a hand-built one uses whatever was passed to the constructor).</summary>
        public float ModelScale => _modelScale;

        /// <summary>The skinned mesh handle this avatar draws (for a game that wants to unload it on teardown via
        /// <c>Scene3D.UnloadSkinnedMesh</c>).</summary>
        public SkinnedMeshHandle Mesh => _mesh;

        /// <summary>Compose an avatar from already-built pieces.</summary>
        /// <param name="controller">The movement body. Not null.</param>
        /// <param name="animation">The animation brain. Not null.</param>
        /// <param name="mesh">The skinned mesh handle to draw (from <c>Scene3D.LoadSkinnedMesh</c>).</param>
        /// <param name="modelScale">Uniform scale applied when drawing (1 = the rig's authored size).</param>
        /// <param name="initialFacingYaw">Starting facing yaw (radians), e.g. a spawn heading.</param>
        public CharacterAvatar(CharacterController3D controller, AnimatedCharacter animation,
            SkinnedMeshHandle mesh, float modelScale = 1f, float initialFacingYaw = 0f)
        {
            _controller = controller ?? throw new ArgumentNullException(nameof(controller));
            _animation = animation ?? throw new ArgumentNullException(nameof(animation));
            _mesh = mesh;
            _modelScale = modelScale;
            _facingYaw = CharacterFacing.WrapAngle(initialFacingYaw);
            _renderY = controller.Position.Y;   // start drawn exactly at the physics height (no ease-in on spawn)
        }

        /// <summary>Advance the character one frame: move + climb + collision (via the controller), turn to face the
        /// INTENDED move direction (input + camera, never the collision-slid velocity, so a scraped wall cannot spin
        /// the model), and advance the animation from the REAL collision-clamped horizontal speed plus the controller's
        /// grounded / vertical-velocity / swimming state. Parameters mirror
        /// <see cref="CharacterController3D.Update"/> exactly, plus nothing else - a game swaps its hand-rolled glue for
        /// this one call. A non-positive <paramref name="dt"/> (a paused or priming tick) still steps the controller
        /// but leaves the animation pose and facing untouched.</summary>
        /// <param name="input">The immutable input snapshot (WASD + shift + space).</param>
        /// <param name="dt">Frame time in seconds.</param>
        /// <param name="cameraYaw">Follow-camera yaw (radians): the basis for both the move and the facing.</param>
        /// <param name="groundHeight">Terrain height at (x, z). Required.</param>
        /// <param name="groundNormal">Optional ground normal for slope gating.</param>
        /// <param name="physics">Optional physics world for prop/building/stair collision.</param>
        /// <param name="medium">Optional fluid-medium provider for wade/swim.</param>
        public void Update(in InputState input, float dt, float cameraYaw,
            Func<float, float, float> groundHeight,
            Func<float, float, Vector3>? groundNormal = null,
            IPhysicsWorld? physics = null,
            Func<float, float, float, MovementMedium>? medium = null)
        {
            Vector3 prev = _controller.Position;
            _controller.Update(input, dt, cameraYaw, groundHeight, groundNormal, physics, medium);
            if (dt <= 1e-6f) { _renderY = _controller.Position.Y; return; }   // priming/paused: snap, don't animate/turn

            // Ease the DRAW height toward the physics height so a stair's discrete per-step height snaps (a descent
            // drops a whole riser in one physics tick) read as a smooth glide on the model and the camera. Grounded
            // only, capped rate; airborne (jump/fall) or a teleport-sized gap snaps so those stay crisp. X/Z are never
            // smoothed - horizontal stays exactly on the physics position, so movement has no input lag.
            float targetY = _controller.Position.Y;
            if (!_controller.Grounded || RenderHeightSmoothRate <= 0f
                || MathF.Abs(targetY - _renderY) > RenderHeightSnapDistance)
                _renderY = targetY;
            else
                _renderY += Math.Clamp(targetY - _renderY, -RenderHeightSmoothRate * dt, RenderHeightSmoothRate * dt);

            // Face the INTENDED move direction (input steered by camera yaw), NOT the collision-resolved velocity, so a
            // wall/prop the capsule slides along cannot swing or spin the model. Turned at a bounded rate; a stationary
            // character holds its heading.
            _facingYaw = CharacterFacing.TurnTowards(_facingYaw,
                CharacterFacing.IntendedMoveDirection(input, cameraYaw), MaxTurnRate, dt);

            // Animate off the REAL motion: horizontal speed from the XZ position delta (so it reflects collision- and
            // wade-clamping, not just input), and the vertical/swim state straight from the controller.
            Vector3 d = _controller.Position - prev; d.Y = 0f;
            float horizontalSpeed = d.Length() / dt;
            _animation.Update(horizontalSpeed, _controller.Grounded, _controller.VerticalVelocity, _controller.Swimming, dt);
        }

        /// <summary>Draw the character's skinned mesh at the capsule's feet (Position is the capsule centre, feet =
        /// centre - half-height), scaled by <see cref="ModelScale"/> and rotated to <see cref="FacingYaw"/>, tinted
        /// <paramref name="tint"/>.</summary>
        public void Draw(Scene3D scene, Color tint)
        {
            if (scene is null) throw new ArgumentNullException(nameof(scene));
            Vector3 p = RenderPosition;   // physics X/Z + the smoothed draw height, so the model glides on stairs
            float footY = p.Y - _controller.CapsuleHalfHeight;
            Matrix4x4 model = Matrix4x4.CreateScale(_modelScale)
                              * Matrix4x4.CreateRotationY(_facingYaw)
                              * Matrix4x4.CreateTranslation(p.X, footY, p.Z);
            scene.DrawSkinned(_mesh, _animation.Pose, model, tint);
        }

        /// <summary>Draw untinted (white).</summary>
        public void Draw(Scene3D scene) => Draw(scene, Color.White);

        /// <summary>Re-point the facing directly (radians), e.g. on a spawn/teleport so the model does not visibly
        /// rotate from a stale heading. Normalized to (-pi, pi].</summary>
        public void SetFacingYaw(float yaw) => _facingYaw = CharacterFacing.WrapAngle(yaw);

        /// <summary>Load a rigged glTF character, map its clips to the locomotion states, scale it to the controller's
        /// capsule height, and compose an avatar - the standard rig-load path every game was writing by hand. Returns
        /// <c>null</c> (never throws) if the asset is missing/unreadable, has no skeleton, or contains none of the
        /// expected locomotion clips, so the game can fall back to a greybox capsule; <paramref name="onFailure"/>
        /// receives the reason for logging. Clip names are mapped by the <see cref="MapLocomotionClips"/> convention
        /// (<c>Idle/Walk/Run/Jump/Fall/SwimIdle/Swim</c>); a rig with different clip names builds the state map itself
        /// and uses the constructor.</summary>
        /// <param name="scene">The scene to upload the skinned mesh into.</param>
        /// <param name="path">Filesystem path to the .glb/.gltf rig.</param>
        /// <param name="controller">The movement body to drive (its <see cref="CharacterController3D.CapsuleHalfHeight"/>
        /// sets the model scale). Not null.</param>
        /// <param name="thresholds">Walk/run speed thresholds for clip selection. Null uses walk 0.1 / run 9 m/s
        /// (matching the controller's 6/12 walk/run feel).</param>
        /// <param name="crossfade">Seconds to blend between locomotion clips on a state change.</param>
        /// <param name="initialFacingYaw">Starting facing yaw (radians).</param>
        /// <param name="onFailure">Optional callback given the failure reason when the load falls back to null.</param>
        public static CharacterAvatar? TryLoadGltf(Scene3D scene, string path, CharacterController3D controller,
            LocomotionThresholds? thresholds = null, float crossfade = 0.15f, float initialFacingYaw = 0f,
            Action<string>? onFailure = null)
        {
            if (scene is null) throw new ArgumentNullException(nameof(scene));
            if (controller is null) throw new ArgumentNullException(nameof(controller));
            try
            {
                (SkinnedGltfMesh mesh, GltfMaterialMaps maps) = GltfLoader.LoadSkinnedWithMaterial(path);
                if (mesh.Skeleton is null) { onFailure?.Invoke("character glTF has no skeleton"); return null; }
                SkinnedMeshHandle handle = scene.LoadSkinnedMesh(mesh, maps);

                var byName = new Dictionary<string, AnimationClip>(StringComparer.Ordinal);
                foreach (AnimationClip c in GltfLoader.LoadAnimations(path)) byName[c.Name] = c;
                Dictionary<LocomotionState, AnimationClip> clips = MapLocomotionClips(byName);
                if (clips.Count == 0)
                {
                    scene.UnloadSkinnedMesh(handle);
                    onFailure?.Invoke("character glTF has none of the expected locomotion clips (Idle/Walk/Run/Jump/Fall/Swim/SwimIdle)");
                    return null;
                }

                float modelHeight = ModelHeight(mesh);
                float modelScale = modelHeight > 0.01f ? (controller.CapsuleHalfHeight * 2f) / modelHeight : 1f;
                var animation = new AnimatedCharacter(mesh.Skeleton, clips,
                    thresholds ?? new LocomotionThresholds(0.1f, 9f), crossfade);
                return new CharacterAvatar(controller, animation, handle, modelScale, initialFacingYaw);
            }
            catch (Exception e)
            {
                onFailure?.Invoke(e.Message);
                return null;
            }
        }

        /// <summary>Map clips keyed by name to the locomotion states by the standard convention (the state enum names:
        /// <c>Idle</c>, <c>Walk</c>, <c>Run</c>, <c>Jump</c>, <c>Fall</c>, <c>SwimIdle</c>, <c>Swim</c>, plus the
        /// pose-override <c>Downed</c> death clip). Only the present names are mapped; a rig missing a state degrades
        /// gracefully at play time (a missing locomotion state falls back to Idle in <see cref="AnimatedCharacter"/>, a
        /// missing <c>Downed</c> clip makes the downed pose collapse procedurally instead). Exposed so a game can reuse
        /// the convention when it builds an <see cref="AnimatedCharacter"/> itself.</summary>
        public static Dictionary<LocomotionState, AnimationClip> MapLocomotionClips(
            IReadOnlyDictionary<string, AnimationClip> clipsByName)
        {
            if (clipsByName is null) throw new ArgumentNullException(nameof(clipsByName));
            var clips = new Dictionary<LocomotionState, AnimationClip>();
            void Map(LocomotionState state, string name)
            {
                if (clipsByName.TryGetValue(name, out AnimationClip? clip)) clips[state] = clip;
            }
            Map(LocomotionState.Idle, "Idle");
            Map(LocomotionState.Walk, "Walk");
            Map(LocomotionState.Run, "Run");
            Map(LocomotionState.Jump, "Jump");
            Map(LocomotionState.Fall, "Fall");
            Map(LocomotionState.SwimIdle, "SwimIdle");
            Map(LocomotionState.Swim, "Swim");
            Map(LocomotionState.Downed, "Downed");
            return clips;
        }

        // Model-space height (max - min vertex Y) of the rest mesh, for the capsule-match scale.
        static float ModelHeight(SkinnedGltfMesh mesh)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (SkinnedVertex v in mesh.Vertices)
            {
                min = MathF.Min(min, v.Position.Y);
                max = MathF.Max(max, v.Position.Y);
            }
            return max > min ? max - min : 0f;
        }
    }
}
