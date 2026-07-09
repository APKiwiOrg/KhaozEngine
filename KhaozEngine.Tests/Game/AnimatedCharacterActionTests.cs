using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Game
{
    // One-shot ACTIONS layered over locomotion on AnimatedCharacter: an action composes over a running locomotion base
    // (lower body tracks locomotion, upper tracks the attack through the whole in/play/out lifecycle), byte-stability
    // when no action plays, and the remote-character path (a game driving a replicated remote's brain by calling
    // PlayAction). Pose math is pure CPU, so everything runs GPU-free.
    [Collection("AllocSensitive")]
    public class AnimatedCharacterActionTests
    {
        // A two-node flat rig: node 0 = "legs" (lower body), node 1 = "arms" (upper body). Independent roots so each
        // composed bone WORLD transform equals its node LOCAL translation (read back by decomposing the palette).
        static Skeleton LegsArms()
        {
            var rest = new[] { JointPose.Identity, JointPose.Identity };
            return new Skeleton(new[] { -1, -1 }, rest, new[] { 0, 1 }, new[] { 0, 1 });
        }

        // A locomotion clip parks BOTH nodes at (x,0,0) so which locomotion clip is playing is legible on either node.
        static AnimationClip Loco(string name, float x)
        {
            var legs = new JointTrack(0)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(x, 0, 0), new Vector3(x, 0, 0) }, InterpolationMode.Linear),
            };
            var arms = new JointTrack(1)
            {
                Translation = new Vector3Track(new[] { 0f, 1f }, new[] { new Vector3(x, 0, 0), new Vector3(x, 0, 0) }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, 1f, new List<JointTrack> { legs, arms });
        }

        static Dictionary<LocomotionState, AnimationClip> Clips() => new()
        {
            [LocomotionState.Idle] = Loco("idle", 1f),
            [LocomotionState.Walk] = Loco("walk", 2f),
            [LocomotionState.Run] = Loco("run", 3f),
        };

        // An upper-body attack: drives node 1 (arms) to X=20 for a while.
        static AnimationClip Attack(float duration = 0.6f)
        {
            var arms = new JointTrack(1)
            {
                Translation = new Vector3Track(new[] { 0f, duration }, new[] { new Vector3(20, 0, 0), new Vector3(20, 0, 0) }, InterpolationMode.Linear),
            };
            return new AnimationClip("attack", duration, new List<JointTrack> { arms });
        }

        static JointPose[] Locals(AnimatedCharacter c)
        {
            Matrix4x4[] pose = c.Pose;
            var outp = new JointPose[pose.Length];
            for (int i = 0; i < pose.Length; i++) outp[i] = JointPose.FromMatrix(pose[i]);
            return outp;
        }

        // Node 0 (legs) upper-body mask that is 0 on legs, 1 on arms.
        static BoneMask UpperBody() => new BoneMask(new[] { 0f, 1f });

        // Settle onto a locomotion state (past crossfade + debounce).
        static void Settle(AnimatedCharacter c, float speed) { for (int i = 0; i < 90; i++) c.Update(speed, true, 0f, 1f / 60f); }

        [Fact]
        public void ActionOverRunningLocomotion_LowerBodyTracksLoco_UpperTracksAttack()
        {
            Skeleton skel = LegsArms();
            var c = new AnimatedCharacter(skel, Clips(), crossfade: 0.05f, stateDebounceSeconds: 0f);
            Settle(c, 5f);   // running: both nodes at X=3

            Assert.False(c.HasActiveActions);
            Assert.Equal(3f, Locals(c)[0].Translation.X, 2);
            Assert.Equal(3f, Locals(c)[1].Translation.X, 2);

            c.PlayAction(Attack(0.6f), UpperBody(), fadeIn: 0.1f, fadeOut: 0.1f);
            Assert.True(c.HasActiveActions);

            // Drive into the sustain, keeping the run input. Legs must still read the run clip (X=3), arms the attack.
            const float dt = 1f / 60f;
            for (float t = 0f; t < 0.35f; t += dt) c.Update(5f, true, 0f, dt);
            Assert.Equal(3f, Locals(c)[0].Translation.X, 2);    // legs: locomotion base unaffected
            Assert.Equal(20f, Locals(c)[1].Translation.X, 1);   // arms: the attack drives them

            // Play out: the action retires and the arms return to tracking locomotion (X=3).
            for (float t = 0f; t < 0.6f; t += dt) c.Update(5f, true, 0f, dt);
            Assert.False(c.HasActiveActions);
            Assert.Equal(3f, Locals(c)[0].Translation.X, 2);
            Assert.Equal(3f, Locals(c)[1].Translation.X, 2);
        }

        [Fact]
        public void NoAction_ByteIdenticalToPlainAnimatedCharacter()
        {
            // A character that plays an action then lets it retire must produce a pose BYTE-identical to a pristine
            // character driven the same way, proving the action path leaves no residue and the base stays byte-stable.
            Skeleton skel = LegsArms();
            var reference = new AnimatedCharacter(skel, Clips(), crossfade: 0.05f, stateDebounceSeconds: 0f);
            var withAction = new AnimatedCharacter(skel, Clips(), crossfade: 0.05f, stateDebounceSeconds: 0f);

            Settle(reference, 2f);
            Settle(withAction, 2f);

            // withAction plays + fully retires an action; reference does nothing.
            withAction.PlayAction(Attack(0.3f), UpperBody(), fadeIn: 0.05f, fadeOut: 0.05f);
            const float dt = 1f / 60f;
            for (float t = 0f; t < 0.5f; t += dt) withAction.Update(2f, true, 0f, dt);
            for (float t = 0f; t < 0.5f; t += dt) reference.Update(2f, true, 0f, dt);
            Assert.False(withAction.HasActiveActions);

            // Both now on the same locomotion input with no action: identical bytes.
            reference.Update(2f, true, 0f, dt);
            withAction.Update(2f, true, 0f, dt);
            Matrix4x4[] a = reference.Pose;
            Matrix4x4[] b = withAction.Pose;
            for (int i = 0; i < a.Length; i++) Assert.Equal(a[i], b[i]);   // exact
        }

        [Fact]
        public void CancelAction_FadesCleanly_NoPop()
        {
            Skeleton skel = LegsArms();
            var c = new AnimatedCharacter(skel, Clips(), crossfade: 0.05f, stateDebounceSeconds: 0f);
            Settle(c, 5f);
            ActionHandle h = c.PlayAction(Attack(5f), UpperBody(), fadeIn: 0.1f, fadeOut: 0.2f);

            const float dt = 1f / 60f;
            for (float t = 0f; t < 0.5f; t += dt) c.Update(5f, true, 0f, dt);   // into sustain: arms at X=20
            float before = Locals(c)[1].Translation.X;
            Assert.Equal(20f, before, 1);

            Assert.True(c.CancelAction(h));
            c.Update(5f, true, 0f, dt);
            float justAfter = Locals(c)[1].Translation.X;
            Assert.True(MathF.Abs(justAfter - before) < 4f, $"pop at cancel: {before} -> {justAfter}");

            for (float t = 0f; t < 0.3f; t += dt) c.Update(5f, true, 0f, dt);
            Assert.False(c.HasActiveActions);
            Assert.Equal(3f, Locals(c)[1].Translation.X, 2);   // arms back on locomotion
        }

        // ---- remote-character path: a game receives a replicated trigger and calls PlayAction on the remote's brain ----

        [Fact]
        public void RemoteCharacter_DrivenByReplicatedBridge_PlaysActionOnItsBrain()
        {
            // The bridge builds a brain per replicated entity from position-only samples (a remote). The game receives
            // an action TRIGGER as a game message (out of scope to replicate here) and must be able to reach that
            // remote's AnimatedCharacter and call PlayAction. We exercise exactly that path headlessly: build the set,
            // step it so the remote brain exists, then look up the brain and play an action on it.
            Skeleton skel = LegsArms();
            var set = new ReplicatedCharacterAnimators(skel, Clips());

            const long remoteId = 42;
            const float dt = 1f / 60f;
            // Feed a moving remote so it runs, and let the brain come into existence + settle onto a loco state.
            var pos = new Vector3(0, 0, 0);
            for (int i = 0; i < 120; i++)
            {
                pos += new Vector3(0.05f, 0, 0);   // steady motion -> Walk/Run
                var samples = new List<CharacterSample> { new CharacterSample(remoteId, pos) };
                set.Update(samples, dt);
            }
            // The game reaches the replicated remote's brain via the bridge and plays the action on it.
            AnimatedCharacter? remote = set.BrainFor(remoteId);
            Assert.NotNull(remote);

            // The remote's local animator API is fully callable (no ownership/authority gate): play an upper-body action.
            ActionHandle h = remote!.PlayAction(Attack(0.5f), UpperBody(), fadeIn: 0.05f, fadeOut: 0.05f);
            Assert.True(h.IsValid);
            Assert.True(remote.HasActiveActions);

            // Keep driving the bridge; the remote's arms now read the attack while its legs keep tracking locomotion.
            for (int i = 0; i < 18; i++)
            {
                pos += new Vector3(0.05f, 0, 0);
                set.Update(new List<CharacterSample> { new CharacterSample(remoteId, pos) }, dt);
            }
            JointPose[] locals = Locals(remote);
            Assert.Equal(20f, locals[1].Translation.X, 1);           // arms: the attack
            Assert.NotEqual(20f, MathF.Round(locals[0].Translation.X)); // legs: still on locomotion, not the attack

            // Play it out: retires cleanly.
            for (int i = 0; i < 45; i++)
            {
                pos += new Vector3(0.05f, 0, 0);
                set.Update(new List<CharacterSample> { new CharacterSample(remoteId, pos) }, dt);
            }
            Assert.False(remote.HasActiveActions);
        }

        // ---- held action pass-through: hold:true threads AnimatedCharacter.PlayAction -> LayeredAnimator ----

        [Fact]
        public void HeldAction_KeepsCharacterActive_PastClipDuration_ThenCancelReleases()
        {
            // A held masked action keeps the character on the compositor path (arms hold their pose over running legs)
            // indefinitely, past the clip duration, until CancelAction fades it out and the character returns to the
            // byte-stable single-player path. Proves the hold flag threads through the AnimatedCharacter wrapper.
            Skeleton skel = LegsArms();
            var c = new AnimatedCharacter(skel, Clips(), crossfade: 0.05f, stateDebounceSeconds: 0f);
            Settle(c, 5f);   // running: both nodes at X=3
            Assert.False(c.HasActiveActions);

            // Hold an upper-body pose (arms -> X=20) while the legs keep running.
            ActionHandle h = c.PlayAction(Attack(1f), UpperBody(), fadeIn: 0.1f, fadeOut: 0.15f, hold: true);
            const float dt = 1f / 60f;

            // Drive well past the 1s clip: a one-shot would have retired; the held action keeps the arms posed.
            for (float t = 0f; t < 2f; t += dt) c.Update(5f, true, 0f, dt);
            Assert.True(c.HasActiveActions);
            Assert.Equal(3f, Locals(c)[0].Translation.X, 2);    // legs still running
            Assert.Equal(20f, Locals(c)[1].Translation.X, 1);   // arms held past the clip end

            // Sheathe: cancel releases the hold and the character returns to pure locomotion.
            Assert.True(c.CancelAction(h));
            for (float t = 0f; t < 0.3f; t += dt) c.Update(5f, true, 0f, dt);
            Assert.False(c.HasActiveActions);
            Assert.Equal(3f, Locals(c)[1].Translation.X, 2);    // arms back on locomotion
        }
    }
}
