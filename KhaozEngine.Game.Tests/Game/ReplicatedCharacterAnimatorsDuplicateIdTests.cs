using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    /// <summary>
    /// ONE POSE PER ENTITY ID, WHATEVER THE CALLER HANDS IN
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/97">#97</see>).
    /// <c>ReplicatedCharacterAnimators.Live</c> is documented as the live characters this frame and every consumer
    /// iterates it to draw, so two entries for one id draw that character twice. The loop used to add each sample's
    /// id to the seen set without checking, and both pose branches push unconditionally, so a sample list with a
    /// repeated id produced a duplicate pose.
    ///
    /// <para>The netcode's own snapshot cannot repeat an id (its samples come out of a dictionary keyed by id), which
    /// is why this was a lead rather than a live bug. A caller that assembles the list another way can: two sources
    /// concatenated without a dedup is the ordinary way to get there, and the bridge is public API that does not get
    /// to assume its one in-tree caller.</para>
    ///
    /// <para>The second cost is the one the pose count hides. Advancing an id twice ages that entry's velocity window,
    /// glide smoother and step baseline twice against a single frame's <c>dt</c>, so the corruption outlives the frame
    /// the duplicate arrived in. <see cref="DuplicateId_DoesNotDoubleAdvanceTheDerivedState"/> is the row for that.</para>
    ///
    /// <para>Lives in its own file because <c>ReplicatedCharacterAnimatorsTests</c> is at its frozen size in
    /// <c>.filesize-baseline</c> and may not grow.</para>
    /// </summary>
    public sealed class ReplicatedCharacterAnimatorsDuplicateIdTests
    {
        const float Dt = 1f / 30f;

        static Skeleton OneBone() =>
            new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        static AnimationClip Park(string name)
        {
            var jt = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, 1f },
                    new[] { Vector3.Zero, Vector3.Zero }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, 1f, new List<JointTrack> { jt });
        }

        static Dictionary<LocomotionState, AnimationClip> Clips() => new()
        {
            [LocomotionState.Idle] = Park("idle"),
            [LocomotionState.Walk] = Park("walk"),
            [LocomotionState.Run] = Park("run"),
            [LocomotionState.Jump] = Park("jump"),
            [LocomotionState.Fall] = Park("fall"),
        };

        static ReplicatedCharacterAnimators NewAnimators() =>
            new(() => new AnimatedCharacter(OneBone(), Clips(), LocomotionThresholds.Default));

        static CharacterSample Pos(int id, Vector3 p) => new(id, p);

        /// <summary>
        /// The headline: a repeated id yields ONE pose, and it is the FIRST sample's (the entity is drawn where the
        /// first entry put it, not smeared onto the last one). The other ids in the same list are untouched.
        /// </summary>
        [Fact]
        public void DuplicateId_InOneSampleSet_ProducesOnePose()
        {
            ReplicatedCharacterAnimators a = NewAnimators();

            a.Update(new[]
            {
                Pos(1, Vector3.Zero),
                Pos(2, new Vector3(5f, 0f, 0f)),
                Pos(1, new Vector3(99f, 0f, 0f)),   // the same entity again, from a second source
            }, Dt);

            Assert.Equal(2, a.Live.Count);
            Assert.Equal(1, a.Live[0].Id);
            Assert.Equal(2, a.Live[1].Id);
            Assert.Equal(0f, a.Live[0].RenderPosition.X, 4);    // the FIRST sample for id 1 is the one posed
            Assert.Equal(5f, a.Live[1].RenderPosition.X, 4);
        }

        /// <summary>
        /// A duplicate must not advance the entry twice either. Both branches of the derived state (the velocity
        /// window and the render-height glide) are per-entry and per-frame, so a second pass over one id integrates
        /// the same frame's dt twice and leaves the entry mis-derived for the frames that follow. Asserted by driving
        /// a straight walk with the id repeated every frame and reading the state back against the same walk driven
        /// with it named once.
        /// </summary>
        [Fact]
        public void DuplicateId_DoesNotDoubleAdvanceTheDerivedState()
        {
            ReplicatedCharacterAnimators clean = NewAnimators();
            ReplicatedCharacterAnimators doubled = NewAnimators();
            var p = Vector3.Zero;

            for (int i = 0; i < 12; i++)
            {
                p += new Vector3(3f * Dt, 0f, 0f);          // a steady 3 m/s walk
                clean.Update(new[] { Pos(1, p) }, Dt);
                doubled.Update(new[] { Pos(1, p), Pos(1, p) }, Dt);
            }

            Assert.Single(doubled.Live);
            Assert.Equal(clean.Live[0].State, doubled.Live[0].State);
            Assert.Equal(clean.Live[0].RenderPosition.X, doubled.Live[0].RenderPosition.X, 4);
            Assert.Equal(clean.Live[0].RenderPosition.Y, doubled.Live[0].RenderPosition.Y, 4);
        }

        /// <summary>
        /// The reaper still sees the id. Dropping the repeat must not drop the ENTITY: the brain built on the first
        /// entry has to survive into the next frame, or a duplicated id would despawn and respawn the character every
        /// frame it arrived twice.
        /// </summary>
        [Fact]
        public void DuplicateId_KeepsTheEntityTracked()
        {
            ReplicatedCharacterAnimators a = NewAnimators();

            a.Update(new[] { Pos(1, Vector3.Zero), Pos(1, Vector3.Zero) }, Dt);
            AnimatedCharacter? first = a.BrainFor(1);
            Assert.NotNull(first);

            a.Update(new[] { Pos(1, Vector3.Zero), Pos(1, Vector3.Zero) }, Dt);
            Assert.Same(first, a.BrainFor(1));   // the same brain, not a fresh one off a reaped entry
        }
    }
}
