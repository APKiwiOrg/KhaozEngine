using System;

namespace KhaozEngine.Game
{
    /// <summary>The clips a <see cref="DirectionalLocomotionBlend"/> chooses between, in four direction families. Each
    /// family has at least one member, ordered by strictly increasing <see cref="GaitClip.FullWeightSpeed"/>, and the
    /// blend splits a speed between the two members that bracket it. The clips are copied into one slot list in the
    /// order forward, backward, left, right. Weights belong to slots, so a clip id may appear in more than one family.
    /// </summary>
    public sealed class DirectionalGaitSet
    {
        internal const int Forward = 0, Backward = 1, Left = 2, Right = 3;

        readonly GaitClip[] _slots;
        // Family f owns slots [_familyStart[f], _familyStart[f + 1]).
        readonly int[] _familyStart = new int[5];

        /// <summary>Copies the four families. Throws <see cref="ArgumentException"/> naming the family when one is
        /// empty, or a member has a <see cref="GaitClip.FullWeightSpeed"/> that is not finite, positive and above its
        /// predecessor's, a <see cref="GaitClip.StrideMetres"/> that is not finite and positive, or a
        /// <see cref="GaitClip.SyncPhase"/> outside <c>[0, 1)</c>.</summary>
        public DirectionalGaitSet(ReadOnlySpan<GaitClip> forward, ReadOnlySpan<GaitClip> backward,
            ReadOnlySpan<GaitClip> left, ReadOnlySpan<GaitClip> right)
        {
            Validate(forward, nameof(forward));
            Validate(backward, nameof(backward));
            Validate(left, nameof(left));
            Validate(right, nameof(right));

            _slots = new GaitClip[forward.Length + backward.Length + left.Length + right.Length];
            int at = 0;
            Append(Forward, forward, ref at);
            Append(Backward, backward, ref at);
            Append(Left, left, ref at);
            Append(Right, right, ref at);
            _familyStart[4] = at;
        }

        /// <summary>The number of slots, which is the sample span length <see cref="DirectionalLocomotionBlend.Advance"/>
        /// requires.</summary>
        public int ClipCount => _slots.Length;

        internal ReadOnlySpan<GaitClip> Slots => _slots;

        internal int FamilyStart(int family) => _familyStart[family];

        internal int FamilyCount(int family) => _familyStart[family + 1] - _familyStart[family];

        void Append(int family, ReadOnlySpan<GaitClip> clips, ref int at)
        {
            _familyStart[family] = at;
            clips.CopyTo(_slots.AsSpan(at));
            at += clips.Length;
        }

        static void Validate(ReadOnlySpan<GaitClip> family, string name)
        {
            if (family.IsEmpty) throw new ArgumentException("A gait family needs at least one clip.", name);
            float previousSpeed = 0f;
            for (int i = 0; i < family.Length; i++)
            {
                GaitClip clip = family[i];
                if (!float.IsFinite(clip.FullWeightSpeed) || clip.FullWeightSpeed <= previousSpeed)
                {
                    throw new ArgumentException(
                        $"Clip {i} FullWeightSpeed {clip.FullWeightSpeed} must be finite, positive and above the previous member's.", name);
                }

                if (!float.IsFinite(clip.StrideMetres) || clip.StrideMetres <= 0f)
                {
                    throw new ArgumentException($"Clip {i} StrideMetres {clip.StrideMetres} must be finite and positive.", name);
                }

                if (!(clip.SyncPhase >= 0f && clip.SyncPhase < 1f))
                {
                    throw new ArgumentException($"Clip {i} SyncPhase {clip.SyncPhase} must be in [0, 1).", name);
                }

                previousSpeed = clip.FullWeightSpeed;
            }
        }
    }
}
