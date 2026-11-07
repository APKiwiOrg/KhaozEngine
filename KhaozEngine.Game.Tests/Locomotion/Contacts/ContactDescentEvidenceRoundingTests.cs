using System.Linq;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public class ContactDescentEvidenceRoundingTests
{
    // #1697. A water-origin handoff can recertify support after changing the reported horizontal axis.
    [Fact]
    public void SelectedSupportUsesThePublishedAxisAfterWaterHandoff()
    {
        var row = SupportGateCostScenes.Rows().Select(r => (SupportCostRow)r[0])
            .Single(r => r.Name == "explicit changed-axis rounding");
        using var scene = new SupportCostExecution(row, "ExplicitTick");
        using var lease = (MovementQueryLease)scene.Producer.Acquire().Lease!;
        var state = new FramedMovementState(row.Input, lease.Frame, new(new("world", "room"), null, lease.Identity));
        var ordinary = ExplicitCharacterMovement.StepTowards(state, row.Direction, false, row.Dt,
            SupportGateCostScenes.Tuning, new(row.WaterMode), lease);
        ContactSupportEvidence? last = null;
        var evidence = new ContactDescentEvidence(Observe);
        bool Observe(in ContactSelectedSupport selected) { last = selected.Facts; return true; }
        lease.DescentEvidence = evidence;
        var observed = ExplicitCharacterMovement.StepTowards(state, row.Direction, false, row.Dt,
            SupportGateCostScenes.Tuning, new(row.WaterMode), lease);
        LocomotionStateBits.AssertEqual(ordinary, observed, LocomotionStateBits.Policy.IncludeContinuityHandle);
        Assert.Equal(MovementStepOutcome.Advanced, observed.Outcome);
        Assert.True(observed.ContactWalkableSupport);
        Assert.NotNull(last);
        Assert.Equal(observed.State.State.Position.X, last.Value.Feet.X);
        Assert.Equal(observed.State.State.Position.Z, last.Value.Feet.Z);
    }
}
