using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.Render3D
{
    /// <summary>One kind of idle moment a standing body plays now and then, such as a glance, a body turn or an ear
    /// flick.</summary>
    /// <param name="Name">The family's name, for the caller's own lookups and the schedule's refusals. The schedule
    /// does not otherwise read it.</param>
    /// <param name="Weight">How often the family is drawn relative to the others in its table. Finite and above
    /// zero. The draw has 16 bits, so a share resolves to 1/65536.</param>
    /// <param name="LengthSeconds">How long one moment of the family plays, seconds. Finite, above zero and no longer
    /// than the room a slot leaves between its quiet ends.</param>
    /// <param name="Mirrored">Whether the family plays to the left or to the right, with even odds, rather than one
    /// way only.</param>
    public readonly record struct MomentFamily(string Name, double Weight, double LengthSeconds, bool Mirrored);

    /// <summary>How a <see cref="MomentSchedule"/> cuts the clock into slots and how often a slot stays empty.</summary>
    /// <param name="SlotSeconds">How long one slot lasts, seconds. Each holds one moment or none. Finite and above
    /// zero.</param>
    /// <param name="QuietSeconds">How long each end of a slot is kept clear of its moment, seconds, so two moments in
    /// back to back slots are never closer than twice this. Finite, zero or more.</param>
    /// <param name="EmptyShare">The share of slots that draw no moment on their own draw, from 0 up to but not
    /// including 1. The cap turns some of those back into moments, so the share of slots that really hold none is
    /// <c>EmptyShare * (1 - EmptyShare^MaxEmptySlots)</c>.</param>
    /// <param name="MaxEmptySlots">The most slots in a row that hold no moment. A slot whose this many slots before
    /// it all drew none on their own draws holds a moment whatever its own draw says. Zero or more.</param>
    /// <param name="Salt">XORed into each body's seed before the hash, so one body's schedule under one salt is
    /// unrelated to its schedule under another. Across a population a salt only permutes which seed gets which
    /// schedule: salt s at seed n is salt 0 at seed n XOR s. Salt 0 leaves the seed as it is.</param>
    public sealed record MomentScheduleOptions(double SlotSeconds, double QuietSeconds, double EmptyShare,
        int MaxEmptySlots, ulong Salt = 0);

    /// <summary>Which way a moment of a mirrored family plays.</summary>
    public enum MomentSide : byte
    {
        /// <summary>No side: no moment, or a moment of a family that plays one way only.</summary>
        None,

        /// <summary>To the body's left.</summary>
        Left,

        /// <summary>To the body's right.</summary>
        Right,
    }

    /// <summary>The idle moment a body is in at one instant.</summary>
    /// <remarks>The default value is no moment, <see cref="None"/>, so a value type built without one carries none
    /// and two such values compare equal. The family is kept one up for that reason and read back as its index.
    /// </remarks>
    public readonly record struct Moment
    {
        // The family's index plus one, so the default is no moment. Wrapping, so every index reads back.
        readonly int _familyPlusOne;

        /// <summary>A moment of a family.</summary>
        /// <param name="family">The family's index in the schedule's table, or -1 for none.</param>
        /// <param name="side">Which way a mirrored family plays, or <see cref="MomentSide.None"/>.</param>
        /// <param name="secondsIn">How far into the moment, seconds, from 0 up to its family's length.</param>
        /// <param name="slotStart">When the slot holding it began on the clock, seconds.</param>
        public Moment(int family, MomentSide side, float secondsIn, double slotStart)
        {
            _familyPlusOne = unchecked(family + 1);
            Side = side;
            SecondsIn = secondsIn;
            SlotStart = slotStart;
        }

        /// <summary>No moment: family -1, no side, 0 s in and a slot start of 0. The default value.</summary>
        public static Moment None => default;

        /// <summary>The family's index in the schedule's table, or -1 for none.</summary>
        public int Family
        {
            get => unchecked(_familyPlusOne - 1);
            init => _familyPlusOne = unchecked(value + 1);
        }

        /// <summary>Which way a mirrored family plays, or <see cref="MomentSide.None"/>.</summary>
        public MomentSide Side { get; init; }

        /// <summary>How far into the moment, seconds, from 0 up to its family's length.</summary>
        public float SecondsIn { get; init; }

        /// <summary>When the slot holding it began on the clock, seconds.</summary>
        public double SlotStart { get; init; }

        /// <summary>Whether this is no moment.</summary>
        public bool IsNone => Family < 0;

        /// <summary>The moment's family, side, seconds in and slot start.</summary>
        /// <param name="family">The family's index, or -1 for none.</param>
        /// <param name="side">Which way it plays.</param>
        /// <param name="secondsIn">How far into it, seconds.</param>
        /// <param name="slotStart">When its slot began, seconds.</param>
        public void Deconstruct(out int family, out MomentSide side, out float secondsIn, out double slotStart)
        {
            family = Family;
            side = Side;
            secondsIn = SecondsIn;
            slotStart = SlotStart;
        }
    }

    /// <summary>When a standing body plays an idle moment and which one: a pure function of the body's id and the
    /// clock over a weighted table of moment families.</summary>
    /// <remarks>
    /// The clock is cut into slots of <see cref="MomentScheduleOptions.SlotSeconds"/>. Each slot holds one moment or
    /// none, decided by a hash of the body's seed and the slot's index. A slot draws none at
    /// <see cref="MomentScheduleOptions.EmptyShare"/>, but one whose
    /// <see cref="MomentScheduleOptions.MaxEmptySlots"/> slots before it all drew none holds a moment anyway. A moment's
    /// family is drawn by weight and a mirrored family's side with even odds. It starts at a hashed offset that keeps
    /// <see cref="MomentScheduleOptions.QuietSeconds"/> clear at both ends of the slot, so it ends inside its slot and
    /// any two moments are at least twice that apart.
    /// <para>NOTHING TO SYNCHRONISE. There is no random source to seed and no state to carry from one frame to the
    /// next: the same seed and clock give the same moment on every machine. The earlier slots' RAW draws are read for
    /// the cap, not whether those slots really held nothing, so a slot is decided by a fixed number of hashes and
    /// never by walking back through the schedule. It still caps the run, because a slot that really held nothing
    /// drew none.</para>
    /// <para>The hash is the SplitMix64 finalizer over the salted seed, stepped by the slot index and finalized again,
    /// so neighbouring ids handed out in sequence draw unrelated schedules. Bits 48 to 63 make the empty draw, bits 32
    /// to 47 the family draw, bit 31 the side (left when clear) and the low 31 bits the start offset. At salt 0 over
    /// a glance and turn table this is Grimhollow's standing-player schedule to the bit.</para>
    /// <para>The table is copied at construction. <see cref="At(long, double)"/> allocates nothing, so it can run per
    /// body per frame.</para>
    /// </remarks>
    public sealed class MomentSchedule
    {
        readonly double _slotSeconds;
        readonly double _quietSeconds;
        readonly double _emptyShare;
        readonly int _maxEmptySlots;
        readonly ulong _salt;

        // Per family: the running weight over the total, the length, the room its start can move in, and whether it
        // is mirrored. The last family takes every draw the others leave, so rounding in the running total never
        // leaves a draw without a family.
        readonly double[] _thresholds;
        readonly double[] _lengths;
        readonly double[] _spans;
        readonly bool[] _mirrored;

        /// <summary>A schedule over a table of moment families.</summary>
        /// <param name="options">The slots, their quiet ends, the empty share, the empty-run cap and the salt.</param>
        /// <param name="families">The moment families, indexed by <see cref="Moment.Family"/>. Copied.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        /// <exception cref="ArgumentException">An option or a family is out of range, or the table is empty. The
        /// message names the option or the family.</exception>
        public MomentSchedule(MomentScheduleOptions options, IReadOnlyList<MomentFamily> families)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(families);
            if (!double.IsFinite(options.SlotSeconds) || options.SlotSeconds <= 0.0)
                throw new ArgumentException($"SlotSeconds is {Text(options.SlotSeconds)}, and a slot must last a"
                    + " finite number of seconds above zero.", nameof(options));
            if (!double.IsFinite(options.QuietSeconds) || options.QuietSeconds < 0.0)
                throw new ArgumentException($"QuietSeconds is {Text(options.QuietSeconds)}, and a quiet end must last"
                    + " a finite number of seconds, zero or more.", nameof(options));
            if (!double.IsFinite(options.EmptyShare) || options.EmptyShare < 0.0 || options.EmptyShare >= 1.0)
                throw new ArgumentException($"EmptyShare is {Text(options.EmptyShare)}, and it must be a share from 0"
                    + " up to but not including 1.", nameof(options));
            if (options.MaxEmptySlots < 0)
                throw new ArgumentException($"MaxEmptySlots is {Text(options.MaxEmptySlots)}, and it cannot be"
                    + " negative.", nameof(options));
            if (families.Count == 0)
                throw new ArgumentException("The schedule has no moment families.", nameof(families));

            _slotSeconds = options.SlotSeconds;
            _quietSeconds = options.QuietSeconds;
            _emptyShare = options.EmptyShare;
            _maxEmptySlots = options.MaxEmptySlots;
            _salt = options.Salt;

            // The room between the quiet ends, in IdleTurns' own order of operations so the starts match its bits.
            double room = _slotSeconds - (2.0 * _quietSeconds);
            int count = families.Count;
            _thresholds = new double[count];
            _lengths = new double[count];
            _spans = new double[count];
            _mirrored = new bool[count];
            double total = 0.0;
            for (int index = 0; index < count; index++)
            {
                MomentFamily family = families[index];
                if (!double.IsFinite(family.Weight) || family.Weight <= 0.0)
                    throw new ArgumentException($"The moment family '{family.Name}' (index {Text(index)}) weighs"
                        + $" {Text(family.Weight)}, and a weight must be a finite number above zero.", nameof(families));
                if (!double.IsFinite(family.LengthSeconds) || family.LengthSeconds <= 0.0)
                    throw new ArgumentException($"The moment family '{family.Name}' (index {Text(index)}) lasts"
                        + $" {Text(family.LengthSeconds)} s, and a moment must last a finite number of seconds above"
                        + " zero.", nameof(families));
                if (family.LengthSeconds > room)
                    throw new ArgumentException($"The moment family '{family.Name}' (index {Text(index)}) lasts"
                        + $" {Text(family.LengthSeconds)} s, longer than the {Text(room)} s a slot of"
                        + $" {Text(_slotSeconds)} s leaves between quiet ends of {Text(_quietSeconds)} s.",
                        nameof(families));
                total += family.Weight;
                _thresholds[index] = total;
                _lengths[index] = family.LengthSeconds;
                _spans[index] = room - family.LengthSeconds;
                _mirrored[index] = family.Mirrored;
            }
            if (!double.IsFinite(total))
                throw new ArgumentException($"The moment families' weights total {Text(total)}, which is not a finite"
                    + " number.", nameof(families));
            // The running weight over the total, not a running sum of shares. Where every running sum and the total
            // are exact in binary, as the humanoid's 0.75 and 1 (or 3 and 4) are, each cut is the exact share and
            // splits the 65536 draws exactly where the table says. Otherwise the rounded sums can move a cut by one
            // draw in 65536.
            for (int index = 0; index < count; index++)
                _thresholds[index] /= total;
        }

        /// <summary>The moment a body is in at this instant, or <see cref="Moment.None"/>.</summary>
        /// <param name="seed">The body's id, such as its net id.</param>
        /// <param name="seconds">The clock, seconds, which never wraps. A clock that is not a finite number has no
        /// moment.</param>
        public Moment At(long seed, double seconds)
        {
            if (!double.IsFinite(seconds)) return Moment.None;
            long slot = (long)Math.Floor(seconds / _slotSeconds);
            ulong stream = Mix(unchecked((ulong)seed ^ _salt));
            ulong hash = Draw(stream, slot);
            if (DrawsEmpty(hash) && !FollowsEmptyDraws(stream, slot)) return Moment.None;
            int family = FamilyOf(Unit(hash, 32));
            MomentSide side = !_mirrored[family]
                ? MomentSide.None
                : ((hash >> 31) & 1UL) == 0UL ? MomentSide.Left : MomentSide.Right;
            double length = _lengths[family];
            double slotStart = slot * _slotSeconds;
            // The low 31 bits over 2^31, spread across every start that leaves the quiet clear at both ends.
            double offset = (hash & 0x7FFFFFFFUL) / 2147483648.0;
            double start = slotStart + _quietSeconds + (offset * _spans[family]);
            double into = seconds - start;
            return into >= 0.0 && into < length ? new Moment(family, side, (float)into, slotStart) : Moment.None;
        }

        /// <summary>The moment a body is in at this instant, skipped when it began before the body last stopped, so
        /// no moment is picked up half way through.</summary>
        /// <param name="seed">The body's id, such as its net id.</param>
        /// <param name="seconds">The clock, seconds.</param>
        /// <param name="stoppedAt">When the body last stopped being busy on that clock, walking or acting, or was
        /// first seen.</param>
        /// <returns><see cref="At(long, double)"/>'s moment, or <see cref="Moment.None"/> when its own start (the
        /// clock less <see cref="Moment.SecondsIn"/>) precedes <paramref name="stoppedAt"/>. A moment that starts at
        /// or after the stop is kept.</returns>
        public Moment At(long seed, double seconds, double stoppedAt)
        {
            Moment moment = At(seed, seconds);
            return !moment.IsNone && seconds - moment.SecondsIn < stoppedAt ? Moment.None : moment;
        }

        // The first family whose running weight over the total exceeds the draw. The last takes whatever is left.
        int FamilyOf(double draw)
        {
            int last = _thresholds.Length - 1;
            for (int index = 0; index < last; index++)
                if (draw < _thresholds[index]) return index;
            return last;
        }

        // Whether a slot's own draw holds no moment.
        bool DrawsEmpty(ulong hash) => Unit(hash, 48) < _emptyShare;

        // Whether every one of the MaxEmptySlots slots before this one drew no moment on its own draw.
        bool FollowsEmptyDraws(ulong stream, long slot)
        {
            for (int back = 1; back <= _maxEmptySlots; back++)
                if (!DrawsEmpty(Draw(stream, slot - back))) return false;
            return true;
        }

        // One draw per slot from a stream seeded by the body: the finalized salted seed, stepped by the slot index,
        // and finalized again. Every bit of the result depends on every bit of both.
        static ulong Draw(ulong stream, long slot) =>
            Mix(unchecked(stream + ((ulong)slot * 0x9E3779B97F4A7C15UL)));

        // The SplitMix64 finalizer.
        static ulong Mix(ulong x)
        {
            x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
            x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
            return x ^ (x >> 31);
        }

        // Sixteen bits of the hash from a shift, over 2^16: a share in [0, 1).
        static double Unit(ulong hash, int shift) => ((hash >> shift) & 0xFFFFUL) / 65536.0;

        static string Text(double value) => value.ToString(CultureInfo.InvariantCulture);

        static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
