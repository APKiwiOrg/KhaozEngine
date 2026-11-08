using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public partial class MovementEnvironmentBufferTests
{
    [Fact]
    public void SeededReusedWaterStorageMatchesColdAcrossEveryRefusalAndMissingWrites()
    {
        // #1728. Reused storage cannot turn a missing write into an old valid contact or
        // retain a partial provider write after a refusal, exception or nested query.
        using var warm = new Scene();
        var random = new Random(1728);
        var observed = new HashSet<MovementAvailability>();
        for (int i = 0; i < 128; i++)
        {
            using var cold = new Scene();
            int kind = i < 12 ? i : random.Next(12);
            warm.Environment.OnCoverage = Handler(warm, kind);
            cold.Environment.OnCoverage = Handler(cold, kind);
            var expectedSpans = Spans(2);
            var actualSpans = Spans(2);
            var expectedContacts = Contacts(2);
            var actualContacts = Contacts(2);
            if (kind == 10)
            {
                Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(() =>
                    Trace(cold.Lease, Sweep, expectedSpans, expectedContacts));
                Assert.Throws<EnvironmentAcquisitionFixture.FixtureException>(() =>
                    Trace(warm.Lease, Sweep, actualSpans, actualContacts));
            }
            else
            {
                var expected = Trace(cold.Lease, Sweep, expectedSpans, expectedContacts);
                var actual = Trace(warm.Lease, Sweep, actualSpans, actualContacts);
                Assert.Equal(expected, actual);
                Assert.Equal(kind switch
                {
                    1 => MovementAvailability.Unresolved,
                    2 => MovementAvailability.CapacityExceeded,
                    4 => MovementAvailability.Stale,
                    5 or 9 => MovementAvailability.PrecisionExceeded,
                    0 or 11 => MovementAvailability.Known,
                    _ => MovementAvailability.Invalid
                }, actual.Availability);
                observed.Add(actual.Availability);
            }
            Assert.Equal(expectedSpans, actualSpans);
            Assert.Equal(expectedContacts, actualContacts);
        }
        Assert.Equal(Enum.GetValues<MovementAvailability>().OrderBy(value => value), observed.OrderBy(value => value));
    }

    static EnvironmentAcquisitionFixture.CoverageHandler Handler(Scene scene, int kind)
    {
        bool nested = false;
        return (in MovementMediumSweepQuery query, Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts) =>
        {
            if (kind == 11 && !nested)
            {
                spans[0] = new(0, 1, 0, 1, true);
                contacts[0] = Contact("river", 0);
                nested = true;
                var innerSpans = new MovementCoverageSpan[2];
                var innerContacts = new MovementDomainContact[2];
                Assert.Equal(MovementAvailability.Known, scene.Lease.TraceWater(query, innerSpans, innerContacts).Availability);
                Assert.Equal(Contact("inner", 0), innerContacts[0]);
                return Complete(scene.Identity);
            }
            if (kind != 7) spans[0] = new(0, 1, 0, 1, true);
            if (kind != 8) contacts[0] = Contact(kind == 11 ? "inner" : "river", 0);
            if (kind == 10) throw new EnvironmentAcquisitionFixture.FixtureException();
            return kind switch
            {
                1 => new(MovementAvailability.Unresolved, 0, 0, 0, 0, 0, scene.Identity),
                2 => new(MovementAvailability.CapacityExceeded, 0, 3, 0, 3, 0, scene.Identity),
                3 => new(MovementAvailability.Invalid, 0, 0, 0, 0, 0, scene.Identity),
                4 => new(MovementAvailability.Stale, 0, 0, 0, 0, 0, scene.Identity),
                5 => new(MovementAvailability.PrecisionExceeded, 0, 0, 0, 0, 0, scene.Identity),
                6 => Complete(WrongIdentity),
                9 => new(MovementAvailability.Known, 1, 1, 1, 1, .01f, scene.Identity),
                _ => Complete(scene.Identity)
            };
        };
    }
}
