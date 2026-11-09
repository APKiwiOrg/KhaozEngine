using System;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

public sealed class MapMembershipFragmentBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UncoveredReplacement_AdmitsOldAndNewStorageBeforeAllocationAndSettlesAtLastUse(bool replacing)
    {
        Check(true); Check(false);
        void Check(bool accepted)
        {
            // A square minus its left half leaves one four-vertex polygon. The next envelope is
            // 128 + 128 + 256 * 4 * (4 + 4). Old storage is 128 initially, then 128 + 128 + 256 * 4.
            const long envelope = 8448, retained = 1280;
            long threshold = envelope + (replacing ? retained : 128);
            var admission = new MapSceneAdmission(new(MaxPreparedBytes: 1000000));
            using var prior = admission.Workspace(); prior.Reserve(100);
            using var receipt = prior.Transfer(100);
            using var validation = new MapPrismValidationWorkspace(admission, new MapRoofPreparationCounts(), 0);
            using var window = admission.LimitPreparation(accepted ? threshold : threshold - 1, "FragmentWindow", 50);
            using var interval = admission.ObservePeak();
            int entered = 0;
            using (var fragments = new MapPrismUncoveredFragments(Square))
            {
                if (replacing) fragments.Subtract(Half);
                long oldBytes = replacing ? retained : 128;
                Assert.Equal(32768 + oldBytes, admission.WorkspaceBytes);
                admission.AllocationAdmitted = increment =>
                {
                    if (increment != envelope) return;
                    entered++;
                    Assert.Equal(32768 + oldBytes + envelope, admission.WorkspaceBytes);
                };
                var error = Record.Exception(() => fragments.Subtract(Half));
                if (accepted)
                {
                    Assert.Null(error);
                    Assert.Equal(1, entered);
                    Assert.Equal(32768 + retained, admission.WorkspaceBytes);
                    Assert.Single(fragments.Polygons);
                    Assert.Equal(4, fragments.Polygons[0].Count);
                    Assert.Equal(32868 + threshold, interval.Counts.Root.AdmittedPeakBytes);
                }
                else
                {
                    var refusal = Assert.IsType<MapSceneCapacityException>(error);
                    Assert.Equal(new MapSceneCapacity("FragmentWindow", 50 + threshold, 50 + threshold - 1), refusal.Capacity);
                    Assert.Equal(0, entered);
                    Assert.Equal(32868 + threshold, interval.Counts.Root.RefusedBytes);
                    Assert.Equal(32768 + oldBytes, admission.WorkspaceBytes);
                    Assert.Single(fragments.Polygons);
                }
            }
            Assert.Equal(32768, admission.WorkspaceBytes);
            Assert.Equal(100, admission.RetainedBytes);
        }
    }
    static MapWideXz P(int x, int z) => MapWideXz.From(new(new(x, 2), new(z, 2)));
    static MapWideXz[] Square => [P(0, 0), P(2, 0), P(2, 2), P(0, 2)];
    static MapWideXz[] Half => [P(0, 0), P(1, 0), P(1, 2), P(0, 2)];
}
