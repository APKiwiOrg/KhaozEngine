using System.Numerics;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.PanZoomTestRig;

namespace KhaozEngine.Tests.Windowing;

public sealed class PanZoomConsumedMouseTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Consumed_admitted_mouse_rejects_continuation_but_allows_fresh_sources_while_held(int source)
    {
        var rig = new PanZoomTestRig(new() { WheelMode = PanZoomWheelMode.Pan });
        rig.Step(Frame(down: true, press: true));
        rig.Step(Frame(new(40, 20), down: true));
        Near(new(20, 0), rig.Target.Translation);
        rig.Input.Update(Frame(new(60, 20), down: true));
        rig.Input.Pointer.ConsumeGesture();
        rig.Controller.Update(rig.Input, rig.Target, Bounds, .01f);
        Near(new(20, 0), rig.Target.Translation);
        Assert.False(rig.Controller.Tapped);

        switch (source)
        {
            case 0:
                rig.Step(Frame(down: true, samples: new[] { Wheel(2, new(50, 50)) }), 0);
                Near(new(20, 80), rig.Target.Translation);
                break;
            case 1:
                rig.Step(Frame(down: true, samples: new[] { Precise(new(0, 3), new(50, 50),
                    PointerGestureInputPhase.Began) }), 0);
                Near(new(20, 3), rig.Target.Translation);
                break;
            case 2:
                rig.Step(Frame(down: true, samples: new[] { Magnify(2, new(50, 50),
                    PointerGestureInputPhase.Began) }), 0);
                Near(2, rig.Target.Scale);
                Near(new(-10, -50), rig.Target.Translation);
                break;
            default:
                rig.Step(Frame(down: true, touches: new[] { StartTouch(1, 20, 20) }));
                rig.Step(Frame(down: true, touches: new[] { Touch(1, 40, 20) }));
                Near(new(40, 0), rig.Target.Translation);
                rig.Step(Frame(down: true, touches: new[] { Touch(1, 40, 20, TouchPhase.Ended) }), .1f);
                break;
        }
        Assert.True(rig.Input.State.IsDown(MouseButton.Left));
        Assert.True(rig.Input.Pointer.IsConsumed);
        Vector2 afterSource = rig.Target.Translation;
        rig.Step(Frame(new(100, 20), down: true));
        rig.Step(Frame(new(100, 20), release: true));
        rig.Step(Frame(), .5f);
        Near(afterSource, rig.Target.Translation);
        Assert.False(rig.Controller.Tapped);
        Assert.False(rig.Controller.DoubleTapped);
        rig.Step(Frame(down: true, press: true));
        rig.Step(Frame(new(30, 20), down: true));
        Near(afterSource + new Vector2(10, 0), rig.Target.Translation);
    }
}
