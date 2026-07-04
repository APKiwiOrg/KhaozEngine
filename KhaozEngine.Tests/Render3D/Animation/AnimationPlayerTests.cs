using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class AnimationPlayerTests
    {
        // Single-bone skeleton (one node, one bone). A clip translates the bone to a constant value, so the
        // composed bone-0 world translation reads back the clip's translation directly.
        static Skeleton OneBone() =>
            new Skeleton(new[] { -1 }, new[] { JointPose.Identity }, new[] { 0 }, new[] { 0 });

        static AnimationClip ConstantTranslationClip(string name, Vector3 value, float duration = 1f)
        {
            var jt = new JointTrack(targetNode: 0)
            {
                Translation = new Vector3Track(new[] { 0f, duration }, new[] { value, value }, InterpolationMode.Linear),
            };
            return new AnimationClip(name, duration, new List<JointTrack> { jt });
        }

        static Vector3 Bone0Translation(AnimationPlayer p)
        {
            Matrix4x4[] palette = p.BonePalette();
            return palette[0].Translation;
        }

        [Fact]
        public void Update_AdvancesTimeAndLoops()
        {
            var p = new AnimationPlayer(OneBone());
            p.Play(ConstantTranslationClip("a", new Vector3(1, 0, 0), duration: 1f));
            p.Update(0.6f);
            Assert.True(p.Time > 0.5f && p.Time < 1f);
            p.Update(0.6f);                       // 1.2 -> wraps to 0.2
            Assert.True(p.Time >= 0f && p.Time < 1f);
        }

        [Fact]
        public void Play_DifferentClip_CrossfadesFromOldToNew()
        {
            var p = new AnimationPlayer(OneBone());
            p.Play(ConstantTranslationClip("a", new Vector3(0, 0, 0)));
            p.Update(0.1f);
            p.Play(ConstantTranslationClip("b", new Vector3(10, 0, 0)), crossfade: 0.2f);
            Assert.True(p.IsBlending);
            // At the very start of the blend the pose is still essentially clip A (0,0,0).
            Assert.True(Bone0Translation(p).X < 1f);
            p.Update(0.2f);                       // blend completes
            Assert.False(p.IsBlending);
            Assert.True(Vector3.Distance(Bone0Translation(p), new Vector3(10, 0, 0)) < 1e-3f);
        }

        [Fact]
        public void Play_SameClip_DoesNotRestartOrBlend()
        {
            var p = new AnimationPlayer(OneBone());
            AnimationClip a = ConstantTranslationClip("a", new Vector3(1, 0, 0));
            p.Play(a);
            p.Update(0.4f);
            float before = p.Time;
            p.Play(a);                            // same instance: no-op
            Assert.False(p.IsBlending);
            Assert.Equal(before, p.Time);
        }

        [Fact]
        public void BonePalette_LengthMatchesBoneCount()
        {
            var p = new AnimationPlayer(OneBone());
            p.Play(ConstantTranslationClip("a", new Vector3(1, 0, 0)));
            Assert.Single(p.BonePalette());
        }

        [Fact]
        public void MidBlend_IsWeightedAverageOfBothClips()
        {
            var p = new AnimationPlayer(OneBone());
            p.Play(ConstantTranslationClip("a", new Vector3(0, 0, 0)));
            p.Update(0.05f);
            p.Play(ConstantTranslationClip("b", new Vector3(4, 0, 0)), crossfade: 0.2f);
            p.Update(0.1f);                       // halfway through the 0.2 blend
            float x = Bone0Translation(p).X;
            Assert.True(x > 1.5f && x < 2.5f, x.ToString());   // ~2 (halfway between 0 and 4)
        }

        [Fact]
        public void Update_SpeedMultiplier_ScalesPlayheadAdvance()
        {
            var p = new AnimationPlayer(OneBone());
            p.Play(ConstantTranslationClip("a", Vector3.Zero, duration: 100f));   // long clip: no wrap
            p.Update(0.1f, 2f);                   // 2x -> playhead advances 0.2
            Assert.Equal(0.2f, p.Time, 4);
        }

        [Fact]
        public void Update_DefaultMultiplier_IsByteIdenticalToSingleArg()
        {
            var a = new AnimationPlayer(OneBone());
            var b = new AnimationPlayer(OneBone());
            a.Play(ConstantTranslationClip("a", new Vector3(1, 0, 0), duration: 100f));
            b.Play(ConstantTranslationClip("a", new Vector3(1, 0, 0), duration: 100f));
            a.Update(0.123f);                     // single-arg (pre-change path)
            b.Update(0.123f, 1f);                 // explicit 1x multiplier
            Assert.Equal(a.Time, b.Time);         // exact, not approximate
            Assert.Equal(a.BonePalette()[0], b.BonePalette()[0]);
        }

        [Fact]
        public void Update_SpeedMultiplier_ScalesPlayhead_ButNotCrossfadeTimer()
        {
            // During a blend the playheads advance at the scaled rate (feet track speed mid-blend), but the
            // crossfade TIMER runs at wall-clock dt so a blend still completes in its authored duration.
            var p = new AnimationPlayer(OneBone());
            p.Play(ConstantTranslationClip("a", Vector3.Zero, duration: 100f));
            p.Update(0.05f);
            p.Play(ConstantTranslationClip("b", new Vector3(4, 0, 0), duration: 100f), crossfade: 0.2f);
            p.Update(0.1f, 5f);                   // dt 0.1 of a 0.2s blend; multiplier 5
            // Crossfade timer used wall-clock dt (0.1/0.2 = 0.5), NOT scaled (which would be 2.5 -> done).
            Assert.True(p.IsBlending);
            // The incoming clip's playhead advanced at the SCALED rate: 0.1 * 5 = 0.5.
            Assert.Equal(0.5f, p.Time, 4);
        }
    }
}
