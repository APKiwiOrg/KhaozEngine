using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Animation.Inspection;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    public class ClipRefusalsTests
    {
        const int RootNode = 0, HipsNode = 1, SpineNode = 2, HeadNode = 3;
        const int RootLogical = 20, HipsLogical = 21, SpineLogical = 22, HeadLogical = 23;

        static readonly string[] Turned = { "hips", "spine", "head" };
        static readonly string[] Moved = { "hips" };

        [Fact]
        public void OnlyRefusesASecondClipOfOneName()
        {
            AnimationClip first = Clip("walk", 1f, Turn(HipsLogical));
            AnimationClip second = Clip("walk", 1f, Turn(SpineLogical));

            Assert.Same(first, ClipRefusals.Only(null, first, "clips"));
            var refusal = Assert.Throws<ArgumentException>(() => ClipRefusals.Only(first, second, "clips"));
            Assert.Equal("clips", refusal.ParamName);
            Assert.StartsWith("The skin carries more than one clip named 'walk'.", refusal.Message,
                StringComparison.Ordinal);
        }

        [Fact]
        public void NoLengthNamesAZeroLengthClip()
        {
            Assert.Equal("The 'walk' clip lasts 0 s, and a locomotion clip must last longer than that.",
                ClipRefusals.NoLength(Clip("walk", 0f), "locomotion"));
            Assert.Equal("The 'swing' clip lasts -0.5 s, and an action clip must last longer than that.",
                ClipRefusals.NoLength(Clip("swing", -0.5f), "action"));
            Assert.Equal("The 'swing' clip lasts NaN s, and an action clip must last longer than that.",
                ClipRefusals.NoLength(Clip("swing", float.NaN), "action"));
        }

        [Fact]
        public void UnkeyedNamesTheFirstJointLeftToTheBind()
        {
            Skeleton skeleton = Build();

            AnimationClip unturned = Clip("walk", 1f, TurnAndMove(HipsLogical), Turn(HeadLogical));
            Assert.Equal("The 'walk' clip does not turn 'spine'. A locomotion clip keys the rotation of each joint it"
                + " must turn, or the joint stands in the bind pose.",
                ClipRefusals.Unkeyed(unturned, skeleton, Turned, Moved, "locomotion"));

            AnimationClip unmoved = Clip("walk", 1f, Turn(HipsLogical), Turn(SpineLogical), Turn(HeadLogical));
            Assert.Equal("The 'walk' clip does not move 'hips'. A locomotion clip keys the translation of each joint"
                + " it must move, or the joint stands at its bind position.",
                ClipRefusals.Unkeyed(unmoved, skeleton, Turned, Moved, "locomotion"));

            // The hips come first in the turned order, so their translation is named before the spine's rotation.
            AnimationClip both = Clip("walk", 1f, Turn(HipsLogical), Turn(HeadLogical));
            Assert.Contains("does not move 'hips'", ClipRefusals.Unkeyed(both, skeleton, Turned, Moved, "locomotion"),
                StringComparison.Ordinal);

            // A joint only moved names is checked after every turned joint.
            AnimationClip rootStill = Clip("swing", 1f, TurnAndMove(HipsLogical), Turn(SpineLogical),
                Turn(HeadLogical));
            Assert.Equal("The 'swing' clip does not move 'root'. An action clip keys the translation of each joint it"
                + " must move, or the joint stands at its bind position.",
                ClipRefusals.Unkeyed(rootStill, skeleton, Turned, new[] { "hips", "root" }, "action"));

            var missing = Assert.Throws<ArgumentException>(
                () => ClipRefusals.Unkeyed(unturned, skeleton, new[] { "tail" }, Moved, "locomotion"));
            Assert.Equal("turned", missing.ParamName);
            Assert.Contains("'tail'", missing.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void UncoveredNamesAStanceChannelLeftUnkeyedOnATakenNode()
        {
            Skeleton skeleton = Build();
            AnimationClip stance = Clip("stance", 0f, Turn(SpineLogical), Move(HipsLogical), Turn(99));
            const string rule = "A clip keys every channel the stance keys";

            AnimationClip unturned = Clip("walk", 1f, TurnAndMove(HipsLogical), Turn(HeadLogical));
            Assert.Equal("The 'walk' clip does not turn 'spine', which the 'stance' clip turns. A clip keys every"
                + " channel the stance keys, or the joint stands in the bind pose, off the stance.",
                ClipRefusals.Uncovered(unturned, skeleton, stance, static _ => true, rule));

            AnimationClip unmoved = Clip("walk", 1f, Turn(HipsLogical), Turn(SpineLogical));
            Assert.Equal("The 'walk' clip does not move 'hips', which the 'stance' clip moves. A clip keys every"
                + " channel the stance keys, or the joint stands in the bind pose, off the stance.",
                ClipRefusals.Uncovered(unmoved, skeleton, stance, static _ => true, rule));

            // The spine is outside the filter, so its unkeyed rotation is not this clip's to key.
            Assert.Null(ClipRefusals.Uncovered(unturned, skeleton, stance, static node => node != SpineNode, rule));
            Assert.Null(ClipRefusals.Uncovered(unturned, skeleton, null, static _ => true, rule));
        }

        [Fact]
        public void BreachNamesTheFirstHygieneFinding()
        {
            Skeleton skeleton = Build();
            var policy = new ClipHygieneOptions(
                TranslationAllowed: new HashSet<string>(StringComparer.Ordinal) { "hips" },
                AllowedNodes: new HashSet<string>(StringComparer.Ordinal) { "root", "hips", "spine", "head" },
                Looping: false,
                MinKeysPerSecond: 0f);

            AnimationClip translated = Clip("walk", 1f, TurnAndMove(HipsLogical), Move(SpineLogical));
            Assert.Equal("The 'walk' clip breaks the locomotion policy: translation on 'spine', translation outside"
                + " TranslationAllowed.",
                ClipRefusals.Breach(translated, skeleton, policy, "locomotion"));

            AnimationClip stray = Clip("swing", 1f, Turn(99));
            Assert.Equal("The 'swing' clip breaks the action policy: unknown-node on a node outside the skeleton,"
                + " logical-index=99.",
                ClipRefusals.Breach(stray, skeleton, policy, "action"));
        }

        [Fact]
        public void CleanClipsAreNotRefused()
        {
            Skeleton skeleton = Build();
            AnimationClip stance = Clip("stance", 0f, Turn(SpineLogical), Move(HipsLogical));
            AnimationClip clean = Clip("walk", 1f, TurnAndMove(HipsLogical), Turn(SpineLogical), Turn(HeadLogical));
            var policy = new ClipHygieneOptions(
                TranslationAllowed: new HashSet<string>(StringComparer.Ordinal) { "hips" },
                AllowedNodes: new HashSet<string>(StringComparer.Ordinal) { "hips", "spine", "head" },
                Looping: true,
                MinKeysPerSecond: 0f);

            Assert.Same(clean, ClipRefusals.Only(null, clean, "clips"));
            Assert.Null(ClipRefusals.NoLength(clean, "locomotion"));
            Assert.Null(ClipRefusals.Unkeyed(clean, skeleton, Turned, Moved, "locomotion"));
            Assert.Null(ClipRefusals.Uncovered(clean, skeleton, stance, static _ => true, "A clip keys the stance"));
            Assert.Null(ClipRefusals.Breach(clean, skeleton, policy, "locomotion"));
        }

        static Skeleton Build() =>
            new(
                new[] { -1, RootNode, HipsNode, SpineNode },
                new[]
                {
                    JointPose.Identity,
                    Pose(new Vector3(0f, 1f, 0f)),
                    Pose(new Vector3(0f, 0.3f, 0f)),
                    Pose(new Vector3(0f, 0.4f, 0f)),
                },
                new[] { RootLogical, HipsLogical, SpineLogical, HeadLogical },
                new[] { HipsNode, SpineNode, HeadNode },
                new[] { "root", "hips", "spine", "head" });

        static AnimationClip Clip(string name, float duration, params JointTrack[] tracks) =>
            new(name, duration, new List<JointTrack>(tracks));

        // A rotation that starts and ends at rest, so a looping policy passes it.
        static JointTrack Turn(int logical) => new(logical) { Rotation = Rotation() };

        static JointTrack Move(int logical) => new(logical) { Translation = Translation() };

        static JointTrack TurnAndMove(int logical) =>
            new(logical) { Rotation = Rotation(), Translation = Translation() };

        static QuaternionTrack Rotation() =>
            new(
                new[] { 0f, 0.5f, 1f },
                new[]
                {
                    Quaternion.Identity,
                    Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.3f),
                    Quaternion.Identity,
                },
                InterpolationMode.Linear);

        static Vector3Track Translation() =>
            new(
                new[] { 0f, 0.5f, 1f },
                new[] { new Vector3(0f, 1f, 0f), new Vector3(0f, 0.95f, 0f), new Vector3(0f, 1f, 0f) },
                InterpolationMode.Linear);

        static JointPose Pose(Vector3 translation) =>
            new() { Translation = translation, Rotation = Quaternion.Identity, Scale = Vector3.One };
    }
}
