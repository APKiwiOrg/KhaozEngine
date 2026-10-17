using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Windowing;
using Xunit;
using static KhaozEngine.Tests.Windowing.PanZoomTestRig;

namespace KhaozEngine.Tests.Windowing;

/// <summary>
/// <see cref="PanZoomController"/> under pointer occlusion: a press whose origin something in front covers is not
/// admitted, and a release that lands on a covered point records no tap.
/// </summary>
public sealed class PanZoomOcclusionTests
{
    static readonly Rect Cover = new(0, 0, 50, 50);

    // One frame, with Cover occluded on the pointer between its update and the controller's when asked.
    static void Step(PanZoomTestRig rig, InputState state, bool occlude, float dt = 0.01f)
    {
        rig.Input.Update(state);
        if (occlude) rig.Input.Pointer.Occlude(Cover);
        rig.Controller.Update(rig.Input, rig.Target, Bounds, dt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Press_on_an_occluded_origin_is_not_admitted(bool occluded)
    {
        var rig = new PanZoomTestRig();
        Step(rig, Frame(new(20, 20), down: true, press: true), occluded);
        Step(rig, Frame(new(40, 20), down: true), occluded);
        Near(occluded ? Vector2.Zero : new Vector2(20, 0), rig.Target.Translation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Release_on_an_occluded_point_records_no_tap(bool occluded)
    {
        var rig = new PanZoomTestRig();
        Step(rig, Frame(new(20, 20), down: true, press: true), occlude: false);
        Step(rig, Frame(new(20, 20), release: true), occluded);
        Assert.Equal(!occluded, rig.Controller.Tapped);
    }
}
