using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Game
{
    /// <summary>A draw-ready character produced by <see cref="ReplicatedCharacterAnimators.Update"/>: the world
    /// transform + the bone palette to hand to <c>Scene3D.DrawSkinned(meshHandle, pose.Pose, pose.World, tint)</c>.
    /// The <see cref="Pose"/> buffer is the brain's own array, reused each frame, so a <see cref="CharacterPose"/> is
    /// valid only until the next <see cref="ReplicatedCharacterAnimators.Update"/>; draw it this frame, do not
    /// retain it.</summary>
    public readonly struct CharacterPose
    {
        public CharacterPose(long id, Matrix4x4 world, float glideFeetY, Matrix4x4[] pose, LocomotionState state, bool isLocal)
        {
            Id = id;
            World = world;
            _glideFeetY = glideFeetY;
            Pose = pose;
            State = state;
            IsLocal = isLocal;
        }

        // The stair-glide-smoothed feet-Y WITHOUT the discrete-step MESH offset (SmoothedY, not drawnFeetY). The DRAW
        // (World / RenderPosition) carries the step-event mesh offset so an isolated riser eases the drawn model. The
        // CAMERA must not inherit that mesh-only smoothing (it would dip the look-at on every curb), so CameraTarget is
        // built off this glide height instead. Equal to the drawn feet-Y whenever no step offset is active.
        readonly float _glideFeetY;

        /// <summary>The entity key this pose belongs to (matches <see cref="CharacterSample.Id"/>).</summary>
        public long Id { get; }

        /// <summary>The world transform: <c>scale * RotationY(facingYaw) * Translation(renderPosition)</c>. The uniform
        /// scale is <see cref="CharacterAnimatorTuning.Scale"/> (default 1), so the consumer can draw with this
        /// matrix directly. The facing yaw assumes the asset's rest pose faces +Z; see
        /// <see cref="CharacterAnimatorTuning.FacingYawOffset"/> for assets that do not. The translation is the SMOOTHED
        /// render position (see <see cref="RenderPosition"/>): the sample X/Z with the slope-glide-smoothed feet-Y, so the
        /// drawn model glides up stairs instead of bobbing per riser.</summary>
        public Matrix4x4 World { get; }

        /// <summary>The presentation position the character is DRAWN at this frame: the sample's X/Z (never smoothed, so
        /// movement stays responsive) with the feet-Y smoothed by the slope-fed stair-glide smoother
        /// (<see cref="CharacterAnimatorTuning.SlopeGlideRate"/>). This is the DRAW anchor - it sits at whatever the
        /// sample carried, which for a feet-anchored sample (the standard: <c>feet = centre - capsuleHalfHeight</c>) is
        /// the FEET. The drawn model already uses it via <see cref="World"/>. On flat ground and while airborne this
        /// equals the raw sample position (the smoother is identity there). Equal to <c>World.Translation</c> by
        /// construction (the smoothed translation is baked into <see cref="World"/>).
        /// <para><b>Do NOT point a follow camera at this directly when the sample is feet-anchored</b> - it drops the
        /// look-at a full capsule half-height below the character (the camera sits at the feet / floor). Use
        /// <see cref="CameraTarget"/> instead, which lifts the glide height back to the capsule centre.</para></summary>
        public Vector3 RenderPosition => World.Translation;

        /// <summary>The point to aim a third-person follow camera at: the stair-glide-smoothed feet height lifted by
        /// <paramref name="capsuleHalfHeight"/> so the look-at sits at the character's CENTRE, not the feet. The bridge
        /// is fed feet-anchored samples (<c>feet = centre - capsuleHalfHeight</c>) so the mesh draws with its feet on the
        /// ground via <see cref="World"/>. <see cref="RenderPosition"/> therefore sits at the feet, which is a full
        /// half-height too low to frame the character. Adding the half-height back reconstructs the smoothed centre - the
        /// same anchor a raw-physics follow camera targets (e.g. <c>WorldClient.LocalRenderState.Position</c>, the capsule
        /// centre) - while keeping the stair GLIDE (so the camera rises/falls smoothly on stairs instead of jolting per
        /// riser). Pass the same half-height used to build the sample.
        /// <para>Unlike <see cref="RenderPosition"/>, this uses the glide height WITHOUT the discrete-step MESH offset
        /// (<see cref="CharacterAnimatorTuning.StepSmoothingRate"/>): that step-event ease is a draw-only smoothing that
        /// keeps the MODEL from popping on an isolated riser, and letting it move the camera would dip the look-at on
        /// every curb/doorstep. So the camera tracks the continuous centre-glide and the mesh alone carries the step
        /// ease. On flat ground and airborne this is exactly the capsule centre (glide + step offset are both identity
        /// there), so a consumer can target it unconditionally.</para></summary>
        public Vector3 CameraTarget(float capsuleHalfHeight) =>
            new(World.Translation.X, _glideFeetY + capsuleHalfHeight, World.Translation.Z);

        /// <summary>Joint-WORLD bone palette for <c>Scene3D.DrawSkinned</c> (a <c>Matrix4x4[]</c>, so it passes
        /// straight to the span-taking draw call - same type as <see cref="AnimatedCharacter.Pose"/>). Transient (see
        /// the type remarks).</summary>
        public Matrix4x4[] Pose { get; }

        /// <summary>The locomotion state chosen this frame (handy for debug overlays).</summary>
        public LocomotionState State { get; }

        /// <summary>True for the local player (forwarded from the sample).</summary>
        public bool IsLocal { get; }

        /// <summary>Compose a piece-local grip through one palette bone and this character's draw transform.
        /// Resolve a named joint to its bone index from the skeleton once, then use that index each frame.</summary>
        public Matrix4x4 ComposeSocket(int boneIndex, in Matrix4x4 pieceLocal) =>
            BoneSocket.Compose(pieceLocal, JointAt(boneIndex), World);

        /// <summary>Compose an attached rigid piece without inheriting the joint's scale or shear. The
        /// character's own <see cref="World"/> scale is retained.</summary>
        public Matrix4x4 ComposeRigidSocket(int boneIndex, in Matrix4x4 pieceLocal) =>
            BoneSocket.ComposeRigid(pieceLocal, JointAt(boneIndex), World);

        Matrix4x4 JointAt(int boneIndex)
        {
            Matrix4x4[]? palette = Pose;
            if (palette is null)
                throw new InvalidOperationException("The character pose has no bone palette.");
            if ((uint)boneIndex >= (uint)palette.Length)
                throw new ArgumentOutOfRangeException(nameof(boneIndex));
            return palette[boneIndex];
        }
    }
}
