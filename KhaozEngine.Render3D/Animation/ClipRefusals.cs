using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Render3D.Animation.Inspection;

namespace KhaozEngine.Render3D
{
    /// <summary>The load-time refusals a skin's clip loader shares, each worded one way and naming the clip, the rule
    /// and the joint: two clips of one name, a clip of no length, a joint a clip must key left to the bind, a channel
    /// the stance keys left unkeyed, and the first finding of a <see cref="ClipHygiene"/> policy.</summary>
    /// <remarks>Each check but <see cref="Only"/> returns null for a clean clip and a message otherwise, so a loader
    /// chains them with <c>??</c> and throws the first. The caller's <c>kind</c> names the clip family in the message,
    /// as in "a locomotion clip" or "an action clip". Pure and GPU-free. Load-time only, as each check allocates.
    /// </remarks>
    public static class ClipRefusals
    {
        /// <summary>The one clip of a name: the clip, or a refusal when a clip of that name was already taken.</summary>
        /// <param name="taken">The clip of that name taken so far, or null.</param>
        /// <param name="clip">The clip of that name just found.</param>
        /// <param name="paramName">The loader's parameter the clips came in by.</param>
        /// <exception cref="ArgumentException">When <paramref name="taken"/> is set.</exception>
        public static AnimationClip Only(AnimationClip? taken, AnimationClip clip, string paramName)
        {
            ArgumentNullException.ThrowIfNull(clip);
            return taken is null
                ? clip
                : throw new ArgumentException($"The skin carries more than one clip named '{clip.Name}'.", paramName);
        }

        /// <summary>Why a clip lasts no time, or null for a clip that lasts longer than zero. A NaN length is
        /// refused too.</summary>
        /// <param name="clip">The clip checked.</param>
        /// <param name="kind">The clip family, as the refusal names it.</param>
        public static string? NoLength(AnimationClip clip, string kind)
        {
            ArgumentNullException.ThrowIfNull(clip);
            ArgumentNullException.ThrowIfNull(kind);
            if (clip.Duration > 0f) return null;
            return $"The '{clip.Name}' clip lasts {clip.Duration.ToString(CultureInfo.InvariantCulture)} s, and"
                + $" {Article(kind)} {kind} clip must last longer than that.";
        }

        /// <summary>Why a clip leaves to the bind a joint that must not stand there, or null when it keys each one:
        /// a joint of <paramref name="turned"/> whose rotation it does not key, or a joint of
        /// <paramref name="moved"/> whose translation it does not key.</summary>
        /// <remarks>The joints are checked in <paramref name="turned"/>'s order, each for its rotation and then, when
        /// <paramref name="moved"/> names it too, its translation. The joints only <paramref name="moved"/> names
        /// follow in its order.</remarks>
        /// <param name="clip">The clip checked.</param>
        /// <param name="skeleton">The skin's skeleton. The clip's tracks name its glTF logical nodes.</param>
        /// <param name="turned">The joints whose rotation the clip must key.</param>
        /// <param name="moved">The joints whose translation the clip must key.</param>
        /// <param name="kind">The clip family, as the refusal names it.</param>
        /// <exception cref="ArgumentException">A joint of <paramref name="turned"/> or <paramref name="moved"/> is
        /// not in the skeleton. The message names it.</exception>
        public static string? Unkeyed(AnimationClip clip, Skeleton skeleton, IReadOnlyList<string> turned,
            IReadOnlyList<string> moved, string kind)
        {
            ArgumentNullException.ThrowIfNull(clip);
            ArgumentNullException.ThrowIfNull(skeleton);
            ArgumentNullException.ThrowIfNull(turned);
            ArgumentNullException.ThrowIfNull(moved);
            ArgumentNullException.ThrowIfNull(kind);
            foreach (string joint in turned) RequireJoint(skeleton, joint, nameof(turned));
            foreach (string joint in moved) RequireJoint(skeleton, joint, nameof(moved));
            foreach (string joint in turned)
            {
                (bool isTurned, bool isMoved) = Keys(clip, skeleton, skeleton.IndexOf(joint));
                if (!isTurned) return NotTurned(clip, joint, kind);
                if (!isMoved && Names(moved, joint)) return NotMoved(clip, joint, kind);
            }
            foreach (string joint in moved)
            {
                if (Names(turned, joint)) continue;
                if (!Keys(clip, skeleton, skeleton.IndexOf(joint)).Moved) return NotMoved(clip, joint, kind);
            }
            return null;
        }

        /// <summary>Why a clip leaves to the bind a channel the stance keys on a node the filter takes, or null when it
        /// keys each one or there is no stance. A stance track on a node outside the skeleton is the joint map's to
        /// refuse.</summary>
        /// <param name="clip">The clip checked.</param>
        /// <param name="skeleton">The skin's skeleton.</param>
        /// <param name="stance">The skin's stance, or null.</param>
        /// <param name="takes">Whether a node of the skeleton is held to the stance's channels.</param>
        /// <param name="rule">The rule the refusal states, before its reason: which clip keys which of the stance's
        /// channels.</param>
        public static string? Uncovered(AnimationClip clip, Skeleton skeleton, AnimationClip? stance,
            Func<int, bool> takes, string rule)
        {
            ArgumentNullException.ThrowIfNull(clip);
            ArgumentNullException.ThrowIfNull(skeleton);
            ArgumentNullException.ThrowIfNull(takes);
            ArgumentNullException.ThrowIfNull(rule);
            if (stance is null) return null;
            foreach (JointTrack keyed in stance.Tracks)
            {
                int node = skeleton.NodeForLogicalIndex(keyed.TargetNode);
                if (node < 0 || !takes(node)) continue;
                (bool turned, bool moved) = Keys(clip, skeleton, node);
                string joint = skeleton.NodeNames[node];
                if (keyed.Rotation is not null && !turned)
                    return $"The '{clip.Name}' clip does not turn '{joint}', which the '{stance.Name}' clip turns."
                        + $" {rule}, or the joint stands in the bind pose, off the stance.";
                if (keyed.Translation is not null && !moved)
                    return $"The '{clip.Name}' clip does not move '{joint}', which the '{stance.Name}' clip moves."
                        + $" {rule}, or the joint stands in the bind pose, off the stance.";
            }
            return null;
        }

        /// <summary>Why a clip breaks a policy, naming its first finding, or null for a clip the policy passes. A track
        /// on a node outside the skeleton has no joint name, so its detail carries the glTF node instead.</summary>
        /// <param name="clip">The clip checked.</param>
        /// <param name="skeleton">The skin's skeleton.</param>
        /// <param name="policy">The policy.</param>
        /// <param name="kind">Which policy, as the refusal names it.</param>
        public static string? Breach(AnimationClip clip, Skeleton skeleton, ClipHygieneOptions policy, string kind)
        {
            ArgumentNullException.ThrowIfNull(kind);
            IReadOnlyList<ClipHygieneFinding> findings = ClipHygiene.Check(clip, skeleton, policy);
            if (findings.Count == 0) return null;
            ClipHygieneFinding first = findings[0];
            string joint = first.NodeName.Length > 0 ? $"'{first.NodeName}'" : "a node outside the skeleton";
            return $"The '{clip.Name}' clip breaks the {kind} policy: {first.Rule} on {joint}, {first.Detail}.";
        }

        // Whether the clip keys a skeleton node's rotation and its translation, over every track on that node.
        static (bool Turned, bool Moved) Keys(AnimationClip clip, Skeleton skeleton, int node)
        {
            bool turned = false;
            bool moved = false;
            foreach (JointTrack track in clip.Tracks)
            {
                if (skeleton.NodeForLogicalIndex(track.TargetNode) != node) continue;
                turned |= track.Rotation is not null;
                moved |= track.Translation is not null;
            }
            return (turned, moved);
        }

        // Refuses a joint name the skeleton does not carry, so a caller's list cannot pass a clip unchecked.
        static void RequireJoint(Skeleton skeleton, string joint, string paramName)
        {
            ArgumentNullException.ThrowIfNull(joint, paramName);
            if (!skeleton.TryIndexOf(joint, out _))
                throw new ArgumentException($"The skeleton has no joint named '{joint}'.", paramName);
        }

        static bool Names(IReadOnlyList<string> joints, string joint)
        {
            foreach (string name in joints)
                if (string.Equals(name, joint, StringComparison.Ordinal)) return true;
            return false;
        }

        static string NotTurned(AnimationClip clip, string joint, string kind) =>
            $"The '{clip.Name}' clip does not turn '{joint}'. {Capital(Article(kind))} {kind} clip keys the rotation"
            + " of each joint it must turn, or the joint stands in the bind pose.";

        static string NotMoved(AnimationClip clip, string joint, string kind) =>
            $"The '{clip.Name}' clip does not move '{joint}'. {Capital(Article(kind))} {kind} clip keys the"
            + " translation of each joint it must move, or the joint stands at its bind position.";

        static string Article(string kind) =>
            kind.Length > 0 && "aeiouAEIOU".Contains(kind[0], StringComparison.Ordinal) ? "an" : "a";

        static string Capital(string article) => article == "an" ? "An" : "A";
    }
}
