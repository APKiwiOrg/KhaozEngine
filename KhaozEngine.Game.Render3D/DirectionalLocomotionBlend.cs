using System;
using System.Numerics;

namespace KhaozEngine.Game
{
    /// <summary>Eight-way locomotion weights over a <see cref="DirectionalGaitSet"/>, with one gait phase shared by
    /// every clip so blended feet stay in step. Presentation only and allocation-free per call.
    ///
    /// <para>Direction: the sign of each body-frame component picks a family, forward or backward for Y and right or
    /// left for X, and a zero component adds none. Each family's share is its travel component over its stride,
    /// <c>|c| / s</c>, normalised over the families, so the blended planted foot moves with the body on both axes.
    /// A family's stride is its members' mix at the current speed, which splits linearly between the two members whose
    /// <see cref="GaitClip.FullWeightSpeed"/> brackets it, clamped to the slowest and fastest. That split also divides
    /// the family's share. A steady target therefore has at most four clips, and a cardinal is one family.</para>
    ///
    /// <para>Easing: each slot weight moves toward its target by at most <c>dt / blendSeconds</c> per call, so a
    /// reversal crossfades through both families for <c>blendSeconds</c>. Below <c>movingSpeed</c> the targets hold
    /// and <see cref="TravelWeight"/> eases to zero instead, so a stop fades out of the last gait.</para>
    ///
    /// <para>Phase: <see cref="Phase"/> advances by <c>(|x| + |y|) x dt / sum(weight x StrideMetres)</c> over the
    /// eased weights, which settles at <c>sum(|c| / s)</c> loops per second and on a cardinal is the speed over the
    /// stride. Each clip is sampled at <c>Phase + SyncPhase</c>. Zero travel holds it. Keep calling
    /// <see cref="Advance"/> while airborne so the gait continues into the landing.</para>
    /// </summary>
    public sealed class DirectionalLocomotionBlend
    {
        // Snaps an eased weight onto its target when float accumulation leaves it short by a rounding error, so N
        // calls of blendSeconds / N land on the target at the Nth call for the call counts a blend sees in practice,
        // rather than leaving a residue for one more. The residue grows with the call count.
        const float SnapEpsilon = 1e-5f;

        readonly DirectionalGaitSet _gaits;
        readonly float _blendSeconds;
        readonly float _movingSpeed;
        readonly float[] _weights;
        readonly float[] _targets;

        /// <summary>Creates a blend at rest. <paramref name="blendSeconds"/> (finite, positive) is the time any weight
        /// takes to travel from 0 to 1. <paramref name="movingSpeed"/> (finite, not negative) is the body speed in m/s
        /// below which the character counts as stopped.</summary>
        public DirectionalLocomotionBlend(DirectionalGaitSet gaits, float blendSeconds = 0.15f, float movingSpeed = 0.05f)
        {
            ArgumentNullException.ThrowIfNull(gaits);
            if (!float.IsFinite(blendSeconds) || blendSeconds <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(blendSeconds), blendSeconds, "Must be finite and positive.");
            }

            if (!float.IsFinite(movingSpeed) || movingSpeed < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(movingSpeed), movingSpeed, "Must be finite and not negative.");
            }

            _gaits = gaits;
            _blendSeconds = blendSeconds;
            _movingSpeed = movingSpeed;
            _weights = new float[gaits.ClipCount];
            _targets = new float[gaits.ClipCount];
        }

        /// <summary>The shared gait phase in <c>[0, 1)</c>, the right-foot contact point of every clip.</summary>
        public float Phase { get; private set; }

        /// <summary>Eases to 1 while moving and to 0 while stopped, over <c>blendSeconds</c>. Use it as the locomotion
        /// layer weight over idle, and as the cue for turn-in-place when it is low and the body yaws.</summary>
        public float TravelWeight { get; private set; }

        /// <summary>Converts a world velocity to the body frame of <paramref name="facingYaw"/> in the
        /// <c>MoveCommand.CameraYaw</c> convention (0 faces -Z, positive turns toward -X). X is right and Y is forward.
        /// The vertical component is ignored.</summary>
        public static Vector2 BodyFrame(Vector3 worldVelocity, float facingYaw)
        {
            float s = MathF.Sin(facingYaw), c = MathF.Cos(facingYaw);
            return new Vector2(worldVelocity.X * c - worldVelocity.Z * s, -worldVelocity.X * s - worldVelocity.Z * c);
        }

        /// <summary>Advances the weights and phase by <paramref name="dt"/> seconds at <paramref name="bodyVelocity"/>
        /// (m/s in the <see cref="BodyFrame"/>), and writes one <see cref="GaitSample"/> per slot with a positive
        /// weight, in slot order. Returns the number written, which is 0 until a movement has eased a weight in after
        /// construction or <see cref="Reset"/>. <paramref name="samples"/> must hold at least
        /// <see cref="DirectionalGaitSet.ClipCount"/> entries.</summary>
        public int Advance(Vector2 bodyVelocity, float dt, Span<GaitSample> samples)
        {
            if (!float.IsFinite(dt) || dt < 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(dt), dt, "Must be finite and not negative.");
            }

            if (!float.IsFinite(bodyVelocity.X) || !float.IsFinite(bodyVelocity.Y))
            {
                throw new ArgumentException("Body velocity must be finite.", nameof(bodyVelocity));
            }

            if (samples.Length < _gaits.ClipCount)
            {
                throw new ArgumentException(
                    $"The sample span holds {samples.Length} but the gait set has {_gaits.ClipCount} clips.", nameof(samples));
            }

            float speed = bodyVelocity.Length();
            bool moving = speed > 0f && speed >= _movingSpeed;
            if (moving) SetTargets(bodyVelocity, speed);

            float step = dt / _blendSeconds;
            float total = 0f;
            for (int i = 0; i < _weights.Length; i++)
            {
                _weights[i] = Ease(_weights[i], _targets[i], step);
                total += _weights[i];
            }

            TravelWeight = Ease(TravelWeight, moving ? 1f : 0f, step);
            if (total <= 0f) return 0;

            ReadOnlySpan<GaitClip> slots = _gaits.Slots;
            float stride = 0f;
            for (int i = 0; i < slots.Length; i++) stride += _weights[i] / total * slots[i].StrideMetres;
            Phase = Frac(Phase + (MathF.Abs(bodyVelocity.X) + MathF.Abs(bodyVelocity.Y)) * dt / stride);

            int written = 0;
            for (int i = 0; i < slots.Length; i++)
            {
                if (_weights[i] <= 0f) continue;
                samples[written++] = new GaitSample(slots[i].ClipId, Frac(Phase + slots[i].SyncPhase), _weights[i] / total);
            }

            return written;
        }

        /// <summary>Returns to rest: every weight, target, <see cref="TravelWeight"/> and <see cref="Phase"/> zero.</summary>
        public void Reset()
        {
            Array.Clear(_weights);
            Array.Clear(_targets);
            Phase = 0f;
            TravelWeight = 0f;
        }

        void SetTargets(Vector2 bodyVelocity, float speed)
        {
            float along = MathF.Abs(bodyVelocity.Y), across = MathF.Abs(bodyVelocity.X);
            int alongLower = 0, acrossLower = 0;
            float alongT = 0f, acrossT = 0f, alongRate = 0f, acrossRate = 0f;
            if (along > 0f)
            {
                int family = bodyVelocity.Y > 0f ? DirectionalGaitSet.Forward : DirectionalGaitSet.Backward;
                alongRate = along / FamilyStride(family, speed, out alongLower, out alongT);
            }

            if (across > 0f)
            {
                int family = bodyVelocity.X > 0f ? DirectionalGaitSet.Right : DirectionalGaitSet.Left;
                acrossRate = across / FamilyStride(family, speed, out acrossLower, out acrossT);
            }

            float rate = alongRate + acrossRate;
            Array.Clear(_targets);
            AddShare(alongLower, alongT, alongRate / rate);
            AddShare(acrossLower, acrossT, acrossRate / rate);
        }

        // The family's stride at speed, the linear mix of the two members whose FullWeightSpeed brackets it, clamped to
        // the slowest and fastest. Returns the lower member's slot and the upper member's part t of the mix.
        float FamilyStride(int family, float speed, out int lower, out float t)
        {
            int start = _gaits.FamilyStart(family);
            int last = start + _gaits.FamilyCount(family) - 1;
            ReadOnlySpan<GaitClip> slots = _gaits.Slots;
            t = 0f;
            if (speed <= slots[start].FullWeightSpeed)
            {
                lower = start;
                return slots[start].StrideMetres;
            }

            if (speed >= slots[last].FullWeightSpeed)
            {
                lower = last;
                return slots[last].StrideMetres;
            }

            lower = start;
            while (slots[lower + 1].FullWeightSpeed <= speed) lower++;
            t = (speed - slots[lower].FullWeightSpeed) / (slots[lower + 1].FullWeightSpeed - slots[lower].FullWeightSpeed);
            return (1f - t) * slots[lower].StrideMetres + t * slots[lower + 1].StrideMetres;
        }

        void AddShare(int lower, float t, float share)
        {
            if (share <= 0f) return;
            _targets[lower] += share * (1f - t);
            if (t > 0f) _targets[lower + 1] += share * t;
        }

        static float Ease(float value, float target, float step)
        {
            float delta = target - value;
            if (MathF.Abs(delta) <= step + SnapEpsilon) return target;
            return value + MathF.CopySign(step, delta);
        }

        static float Frac(float x)
        {
            float f = x - MathF.Floor(x);
            return f >= 1f ? 0f : f;
        }
    }
}
