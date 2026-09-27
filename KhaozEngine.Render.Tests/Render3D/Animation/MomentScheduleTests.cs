using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Render3D.Animation
{
    [Collection("AllocSensitive")]
    public class MomentScheduleTests
    {
        const int Glance = 0, Turn = 1;

        // Grimhollow's standing player: a glance three times in four, a body turn otherwise, both left or right.
        static readonly MomentFamily[] Turns =
        {
            new("glance", 0.75, 2.5, Mirrored: true),
            new("turn", 0.25, 4.0, Mirrored: true),
        };

        static readonly MomentScheduleOptions TurnOptions = new(10.0, 2.0, 0.3, 2);

        // A cow: longer slots, shorter quiet ends, more empty slots and a longer cap, a salt of its own, and single
        // families beside mirrored ones.
        static readonly MomentFamily[] Cow =
        {
            new("ear.flick", 0.4, 1.5, Mirrored: true),
            new("tail.swish", 0.3, 2.0, Mirrored: false),
            new("look", 0.2, 3.5, Mirrored: true),
            new("shift", 0.1, 4.0, Mirrored: false),
        };

        static readonly MomentScheduleOptions CowOptions = new(12.0, 1.5, 0.4, 3, Salt: 0xC0FFEEUL);

        // The finest step a scan takes. Every family above lasts at least six of them, so no moment slips past.
        const double Step = 0.25;

        [Fact]
        public void AtSaltZeroItIsIdleTurnsToTheBit()
        {
            var schedule = new MomentSchedule(TurnOptions, Turns);
            var seen = new int[5];
            const int Steps = 2 * 3600 * 4;
            for (long seed = 1; seed <= 200; seed++)
            {
                for (int step = 0; step <= Steps; step++)
                {
                    double seconds = step * Step;
                    IdleTurnsReference.Turn expected = IdleTurnsReference.At(seed, seconds);
                    seen[(int)expected.Kind]++;
                    Moment actual = schedule.At(seed, seconds);
                    if (!Same(expected, actual))
                        Assert.Fail($"Seed {seed} at {seconds} s: IdleTurns gives {expected}, the schedule {actual}.");

                    double stoppedAt = seconds - 1.0;
                    IdleTurnsReference.Turn expectedKept = IdleTurnsReference.At(seed, seconds, stoppedAt);
                    Moment actualKept = schedule.At(seed, seconds, stoppedAt);
                    if (!Same(expectedKept, actualKept))
                        Assert.Fail($"Seed {seed} at {seconds} s stopped at {stoppedAt} s: IdleTurns gives"
                            + $" {expectedKept}, the schedule {actualKept}.");
                }
            }

            // Every kind turned up, so the comparison covered each family and side as well as none.
            foreach (int count in seen)
                Assert.True(count > 1000, $"A kind turned up only {count} times.");
        }

        [Fact]
        public void ASaltGivesAnIndependentSchedule()
        {
            var plain = new MomentSchedule(TurnOptions, Turns);
            var zero = new MomentSchedule(TurnOptions with { Salt = 0UL }, Turns);
            var salted = new MomentSchedule(TurnOptions with { Salt = 1UL }, Turns);
            const long Seed = 7;
            const int Slots = 1000;

            int differ = 0;
            for (long slot = 0; slot < Slots; slot++)
            {
                SlotMoment unsalted = Scan(plain, Seed, slot, TurnOptions.SlotSeconds);
                Assert.Equal(unsalted, Scan(zero, Seed, slot, TurnOptions.SlotSeconds));
                if (unsalted != Scan(salted, Seed, slot, TurnOptions.SlotSeconds)) differ++;
            }

            // Two independent schedules agree only where both slots are empty, about 7 percent of them.
            Assert.True(differ > Slots * 0.85, $"Salts 0 and 1 disagree on only {differ} of {Slots} slots.");
        }

        [Fact]
        public void TheStatisticsFollowTheTable()
        {
            var schedule = new MomentSchedule(CowOptions, Cow);
            const int Seeds = 100, Slots = 500;
            double slotSeconds = CowOptions.SlotSeconds, quiet = CowOptions.QuietSeconds;
            // SecondsIn is a float, so a start read back from it carries its rounding.
            const double Tolerance = 1e-5;

            var families = new int[Cow.Length];
            var lefts = new int[Cow.Length];
            int empty = 0, longestRun = 0;
            for (long seed = 1; seed <= Seeds; seed++)
            {
                int run = 0;
                double lastEnd = double.NegativeInfinity;
                for (long slot = 0; slot < Slots; slot++)
                {
                    SlotMoment moment = Scan(schedule, seed, slot, slotSeconds);
                    if (moment.Family < 0)
                    {
                        empty++;
                        longestRun = Math.Max(longestRun, ++run);
                        continue;
                    }
                    run = 0;
                    families[moment.Family]++;
                    MomentFamily family = Cow[moment.Family];
                    if (family.Mirrored)
                    {
                        Assert.NotEqual(MomentSide.None, moment.Side);
                        if (moment.Side == MomentSide.Left) lefts[moment.Family]++;
                    }
                    else
                    {
                        Assert.Equal(MomentSide.None, moment.Side);
                    }

                    double slotStart = slot * slotSeconds;
                    double end = moment.Start + family.LengthSeconds;
                    Assert.True(moment.Start >= slotStart + quiet - Tolerance,
                        $"Seed {seed} slot {slot} starts at {moment.Start} s, inside the slot's opening quiet.");
                    Assert.True(end <= slotStart + slotSeconds - quiet + Tolerance,
                        $"Seed {seed} slot {slot} ends at {end} s, inside the slot's closing quiet.");
                    Assert.True(moment.Start - lastEnd >= (2.0 * quiet) - Tolerance,
                        $"Seed {seed} slot {slot} starts {moment.Start - lastEnd} s after the moment before it.");
                    lastEnd = end;
                }
            }

            int total = Seeds * Slots, held = total - empty;
            double expectedEmpty = CowOptions.EmptyShare
                * (1.0 - Math.Pow(CowOptions.EmptyShare, CowOptions.MaxEmptySlots));
            double emptyShare = (double)empty / total;
            Assert.True(Math.Abs(emptyShare - expectedEmpty) <= 0.02,
                $"{emptyShare} of the slots are empty, expected {expectedEmpty}.");
            // The cap holds and is reached, so it neither leaks nor bites early.
            Assert.Equal(CowOptions.MaxEmptySlots, longestRun);

            double weights = 0.0;
            foreach (MomentFamily family in Cow) weights += family.Weight;
            for (int index = 0; index < Cow.Length; index++)
            {
                double share = (double)families[index] / held, expected = Cow[index].Weight / weights;
                Assert.True(Math.Abs(share - expected) <= 0.03,
                    $"'{Cow[index].Name}' holds {share} of the moments, expected {expected}.");
                if (!Cow[index].Mirrored) continue;
                double left = (double)lefts[index] / families[index];
                Assert.True(Math.Abs(left - 0.5) <= 0.03, $"'{Cow[index].Name}' goes left {left} of the time.");
            }
        }

        [Fact]
        public void AMomentBegunBeforeTheStopIsDropped()
        {
            var schedule = new MomentSchedule(TurnOptions, Turns);
            const long Seed = 3;
            (double seconds, Moment moment) = FirstMoment(schedule, Seed, from: 0.0, secondsIn: 0.5);
            double begun = seconds - moment.SecondsIn;

            // Stopped before it began, or exactly as it began: kept.
            Assert.Equal(moment, schedule.At(Seed, seconds, begun - 5.0));
            Assert.Equal(moment, schedule.At(Seed, seconds, begun));
            // Stopped after it began: dropped, not picked up half way through.
            Assert.Equal(Moment.None, schedule.At(Seed, seconds, begun + Step));
            Assert.Equal(Moment.None, schedule.At(Seed, seconds, seconds));

            // The next moment begins after that stop, so it plays.
            (double later, Moment next) = FirstMoment(schedule, Seed, from: begun + 10.0, secondsIn: 0.0);
            Assert.Equal(next, schedule.At(Seed, later, begun + Step));

            // No moment is none whatever the stop.
            double quiet = Math.Floor(begun / TurnOptions.SlotSeconds) * TurnOptions.SlotSeconds;
            Assert.True(schedule.At(Seed, quiet).IsNone);
            Assert.Equal(Moment.None, schedule.At(Seed, quiet, double.NegativeInfinity));
        }

        [Fact]
        public void ANonFiniteClockHasNoMoment()
        {
            var schedule = new MomentSchedule(TurnOptions, Turns);
            Assert.Equal(-1, Moment.None.Family);
            Assert.Equal(MomentSide.None, Moment.None.Side);
            Assert.Equal(0f, Moment.None.SecondsIn);
            Assert.Equal(0.0, Moment.None.SlotStart);
            Assert.True(Moment.None.IsNone);

            foreach (double clock in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                for (long seed = 1; seed <= 50; seed++)
                {
                    Assert.Equal(Moment.None, schedule.At(seed, clock));
                    Assert.Equal(Moment.None, schedule.At(seed, clock, double.NegativeInfinity));
                }
            }
        }

        [Fact]
        public void ADefaultMomentIsNoMoment()
        {
            Moment unset = default;
            Assert.True(unset.IsNone);
            Assert.Equal(-1, unset.Family);
            Assert.Equal(MomentSide.None, unset.Side);
            Assert.Equal(Moment.None, unset);
            Assert.True(Moment.None == unset);
            Assert.Equal(Moment.None, new Moment(-1, MomentSide.None, 0f, 0.0));

            // A real moment keeps its family index, the first family included.
            foreach (int family in new[] { 0, 1, 3, int.MaxValue })
            {
                var moment = new Moment(family, MomentSide.Right, 1.5f, 20.0);
                Assert.Equal(family, moment.Family);
                Assert.False(moment.IsNone);
                Assert.NotEqual(Moment.None, moment);
                Assert.Equal(family, (moment with { SecondsIn = 0.5f }).Family);
                (int deconstructed, MomentSide side, float secondsIn, double slotStart) = moment;
                Assert.Equal((family, MomentSide.Right, 1.5f, 20.0), (deconstructed, side, secondsIn, slotStart));
            }

            // Every family the schedule plays reads back as its own index.
            var schedule = new MomentSchedule(CowOptions, Cow);
            var seen = new bool[Cow.Length];
            for (int step = 0; step < 40_000; step++)
            {
                Moment moment = schedule.At(9, step * Step);
                if (moment.IsNone) continue;
                Assert.InRange(moment.Family, 0, Cow.Length - 1);
                seen[moment.Family] = true;
            }
            Assert.All(seen, Assert.True);
        }

        [Fact]
        public void RefusesABadTable()
        {
            MomentFamily glance = Turns[Glance];
            Assert.Throws<ArgumentNullException>(() => new MomentSchedule(null!, Turns));
            Assert.Throws<ArgumentNullException>(() => new MomentSchedule(TurnOptions, null!));

            Refused(TurnOptions, Array.Empty<MomentFamily>(), "families", "no moment families");
            foreach (double weight in new[] { 0.0, -0.25, double.NaN, double.PositiveInfinity })
                Refused(TurnOptions, new[] { glance with { Weight = weight } }, "families", "'glance'", "weighs");
            Refused(TurnOptions, new[] { glance with { Weight = double.MaxValue }, Turns[Turn] with
                { Weight = double.MaxValue } }, "families", "weights total");
            foreach (double length in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, 6.001 })
                Refused(TurnOptions, new[] { glance with { LengthSeconds = length } }, "families", "'glance'", "lasts");

            foreach (double slot in new[] { 0.0, -10.0, double.NaN, double.PositiveInfinity })
                Refused(TurnOptions with { SlotSeconds = slot }, Turns, "options", "SlotSeconds");
            foreach (double quiet in new[] { -0.5, double.NaN, double.PositiveInfinity })
                Refused(TurnOptions with { QuietSeconds = quiet }, Turns, "options", "QuietSeconds");
            foreach (double share in new[] { -0.1, 1.0, 1.5, double.NaN, double.NegativeInfinity })
                Refused(TurnOptions with { EmptyShare = share }, Turns, "options", "EmptyShare");
            Refused(TurnOptions with { MaxEmptySlots = -1 }, Turns, "options", "MaxEmptySlots");

            // The edges are taken: a moment filling the room between the quiet ends, no quiet, no empty share and
            // no empty slot allowed. With neither empty draws nor a run of them allowed, every slot holds a moment.
            _ = new MomentSchedule(TurnOptions, new[] { glance with { LengthSeconds = 6.0 } });
            _ = new MomentSchedule(TurnOptions with { QuietSeconds = 0.0 }, new[] { glance with { LengthSeconds = 10.0 } });
            var full = new MomentSchedule(TurnOptions with { MaxEmptySlots = 0 }, Turns);
            var none = new MomentSchedule(TurnOptions with { EmptyShare = 0.0 }, Turns);
            for (long slot = 0; slot < 200; slot++)
            {
                Assert.True(Scan(full, 5, slot, TurnOptions.SlotSeconds).Family >= 0, $"Slot {slot} is empty.");
                Assert.True(Scan(none, 5, slot, TurnOptions.SlotSeconds).Family >= 0, $"Slot {slot} is empty.");
            }
        }

        [Fact]
        public void TheTableIsCopied()
        {
            var families = new List<MomentFamily>(Turns);
            var schedule = new MomentSchedule(TurnOptions, families);
            var reference = new MomentSchedule(TurnOptions, Turns);
            families.Clear();
            families.Add(new MomentFamily("other", 1.0, 1.0, Mirrored: false));

            for (int step = 0; step < 4000; step++)
                Assert.Equal(reference.At(11, step * Step), schedule.At(11, step * Step));
        }

        [Fact]
        public void AtAllocatesNothing()
        {
            var schedule = new MomentSchedule(CowOptions, Cow);
            float sink = schedule.At(1, 1.0).SecondsIn + schedule.At(1, 1.0, 0.5).SecondsIn;

            AllocAssert.NoPerCallAllocation("MomentSchedule.At", () =>
            {
                for (int call = 0; call < 1000; call++)
                {
                    sink += schedule.At(call, call * 0.37).SecondsIn;
                    sink += schedule.At(call, call * 0.37, call * 0.1).SecondsIn;
                }
            });
            Assert.True(float.IsFinite(sink));
        }

        static void Refused(MomentScheduleOptions options, MomentFamily[] families, string paramName,
            params string[] fragments)
        {
            var refusal = Assert.Throws<ArgumentException>(() => new MomentSchedule(options, families));
            Assert.Equal(paramName, refusal.ParamName);
            foreach (string fragment in fragments)
                Assert.Contains(fragment, refusal.Message, StringComparison.Ordinal);
        }

        // The moment a slot holds, read back from the schedule at every step across the slot, or none. Every step
        // that lands in the moment must agree on it.
        static SlotMoment Scan(MomentSchedule schedule, long seed, long slot, double slotSeconds)
        {
            double slotStart = slot * slotSeconds;
            SlotMoment found = SlotMoment.Empty;
            for (int step = 0; step * Step < slotSeconds; step++)
            {
                double seconds = slotStart + (step * Step);
                Moment moment = schedule.At(seed, seconds);
                if (moment.IsNone) continue;
                Assert.Equal(slotStart, moment.SlotStart);
                var here = new SlotMoment(moment.Family, moment.Side, seconds - moment.SecondsIn);
                if (found.Family < 0)
                {
                    found = here;
                    continue;
                }
                Assert.Equal(found.Family, here.Family);
                Assert.Equal(found.Side, here.Side);
                Assert.True(Math.Abs(found.Start - here.Start) < 1e-5, $"Slot {slot} holds two starts.");
            }
            return found;
        }

        static (double Seconds, Moment Moment) FirstMoment(MomentSchedule schedule, long seed, double from,
            double secondsIn)
        {
            for (int step = 0; step < 100_000; step++)
            {
                double seconds = from + (step * Step);
                Moment moment = schedule.At(seed, seconds);
                if (!moment.IsNone && moment.SecondsIn >= secondsIn) return (seconds, moment);
            }
            throw new InvalidOperationException("No moment turned up.");
        }

        static bool Same(IdleTurnsReference.Turn expected, Moment actual)
        {
            (int family, MomentSide side) = expected.Kind switch
            {
                IdleTurnsReference.Kind.GlanceLeft => (Glance, MomentSide.Left),
                IdleTurnsReference.Kind.GlanceRight => (Glance, MomentSide.Right),
                IdleTurnsReference.Kind.TurnLeft => (Turn, MomentSide.Left),
                IdleTurnsReference.Kind.TurnRight => (Turn, MomentSide.Right),
                _ => (-1, MomentSide.None),
            };
            return actual.Family == family
                && actual.Side == side
                && BitConverter.SingleToInt32Bits(actual.SecondsIn) == BitConverter.SingleToInt32Bits(expected.SecondsIn)
                && BitConverter.DoubleToInt64Bits(actual.SlotStart) == BitConverter.DoubleToInt64Bits(expected.SlotStart);
        }

        // A slot's moment: its family, side and start on the clock, or a family of -1 for none.
        readonly record struct SlotMoment(int Family, MomentSide Side, double Start)
        {
            public static SlotMoment Empty => new(-1, MomentSide.None, 0.0);
        }

        // A verbatim port of Grimhollow's IdleTurns (Grimhollow.Core/Client/IdleTurns.cs), which the engine cannot
        // reference. The schedule must reproduce it to the bit at salt 0.
        static class IdleTurnsReference
        {
            public enum Kind : byte { None, GlanceLeft, GlanceRight, TurnLeft, TurnRight }

            public readonly record struct Turn(Kind Kind, float SecondsIn, double SlotStart);

            const double SlotSeconds = 10.0;
            const double QuietSeconds = 2.0;
            const double GlanceSeconds = 2.5;
            const double BodyTurnSeconds = 4.0;
            const double EmptyShare = 0.3;
            const int MaxEmptySlots = 2;
            const double GlanceShare = 0.75;

            static double LengthOf(Kind kind) => kind switch
            {
                Kind.GlanceLeft or Kind.GlanceRight => GlanceSeconds,
                Kind.TurnLeft or Kind.TurnRight => BodyTurnSeconds,
                _ => 0.0,
            };

            public static Turn At(long seed, double seconds)
            {
                if (!double.IsFinite(seconds)) return default;
                long slot = (long)Math.Floor(seconds / SlotSeconds);
                ulong hash = Hash(seed, slot);
                if (DrawsEmpty(hash) && !FollowsEmptyDraws(seed, slot)) return default;
                bool glance = Unit(hash, 32) < GlanceShare;
                bool left = ((hash >> 31) & 1UL) == 0UL;
                Kind kind = glance
                    ? left ? Kind.GlanceLeft : Kind.GlanceRight
                    : left ? Kind.TurnLeft : Kind.TurnRight;
                double length = LengthOf(kind);
                double slotStart = slot * SlotSeconds;
                double offset = (hash & 0x7FFFFFFFUL) / 2147483648.0;
                double start = slotStart + QuietSeconds + (offset * (SlotSeconds - (2.0 * QuietSeconds) - length));
                double into = seconds - start;
                return into >= 0.0 && into < length ? new Turn(kind, (float)into, slotStart) : default;
            }

            public static Turn At(long seed, double seconds, double stoppedAt)
            {
                Turn turn = At(seed, seconds);
                return turn.Kind != Kind.None && seconds - turn.SecondsIn < stoppedAt ? default : turn;
            }

            static bool DrawsEmpty(ulong hash) => Unit(hash, 48) < EmptyShare;

            static bool FollowsEmptyDraws(long seed, long slot)
            {
                for (int back = 1; back <= MaxEmptySlots; back++)
                    if (!DrawsEmpty(Hash(seed, slot - back))) return false;
                return true;
            }

            static ulong Hash(long seed, long slot) =>
                Mix(unchecked(Mix((ulong)seed) + ((ulong)slot * 0x9E3779B97F4A7C15UL)));

            static ulong Mix(ulong x)
            {
                x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
                x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
                return x ^ (x >> 31);
            }

            static double Unit(ulong hash, int shift) => ((hash >> shift) & 0xFFFFUL) / 65536.0;
        }
    }
}
