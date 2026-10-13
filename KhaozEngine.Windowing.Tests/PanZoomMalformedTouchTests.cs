using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.PanZoomTestRig;

namespace KhaozEngine.Tests.Windowing;

public sealed class PanZoomMalformedTouchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undefined_phase_rejects_contact_sequence_until_release_and_fresh_began(bool initial)
    {
        var rig = new PanZoomTestRig();
        if (!initial) rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, initial ? 20 : 40, 20, (TouchPhase)99) }));
        Assert.Equal((TouchPhase)99, rig.Input.State.Touches[0].Phase);
        rig.Step(Frame(touches: new[] { Touch(1, 60, 20) }));
        rig.Step(Frame(touches: new[] { StartTouch(1, 80, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 80, 20), StartTouch(2, 100, 20) }));
        rig.Step(Frame(touches: new[] { Touch(2, 120, 20) }));
        Assert.Empty(rig.Target.Operations);
        rig.Step(Frame(touches: new[] { Touch(2, 120, 20, TouchPhase.Ended) }));
        rig.Step(Frame(), .1f);
        Assert.Empty(rig.Target.Operations);
        Assert.False(rig.Controller.Tapped);
        Assert.False(rig.Controller.DoubleTapped);
        rig.Step(Frame(touches: new[] { StartTouch(1, 20, 20) }));
        rig.Step(Frame(touches: new[] { Touch(1, 40, 20) }));
        Near(new(20, 0), rig.Target.Translation);
    }
}
