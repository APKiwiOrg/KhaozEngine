using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    const float EdgeDrop = 0.1f;

    public static IEnumerable<object[]> SlopeEdgeRows()
    {
        foreach (object[] row in SignalRows())
            foreach (float degrees in new[] { 14f, 30f })
                foreach (bool up in new[] { true, false })
                    yield return [row[0], row[1], row[2], degrees, up];
    }

    // A slab ramp over x 0 to 2. Uphill walks over its top edge onto a floor 0.1 m below it. Downhill walks off its
    // low end onto a floor 0.1 m below it. Past the top edge the support is the edge's level height, so the only
    // step is the authored drop. Off the low end the switch point between samples is unknown, so a drop may read
    // short (#1354) but never with the wrong sign.
    [Theory]
    [MemberData(nameof(SlopeEdgeRows))]
    public void SlopeEdgesReportOnlyTheirDrop(SceneVariant variant, int hz, bool run, float degrees, bool up)
    {
        // 14 degrees is the reviewer's 0.5 m rise over 2 m.
        float rise = degrees == 30f ? 2 * MathF.Tan(Radians(30)) : 0.5f;
        var scene = new FootSupportScene(variant).Slab("ramp", new Vector3(1, rise / 2, 0), MathF.Atan2(rise, 2),
            MathF.Sqrt(4 + rise * rise) / 2, 2);
        if (up) scene.Flat("low", 2, 6, -2, 2, rise - EdgeDrop);
        else scene.Flat("low", -4, 0, -2, 2, -EdgeDrop);
        using var g = new Ground(scene, Abyss);
        float x0 = up ? 0.5f : 1.5f, end = up ? 4f : -2f, direction = up ? 1 : -1;
        MoveState s = Standing(new Vector3(x0, rise / 2 * x0, 0.3f));
        double sum = 0;
        for (int tick = 0; tick < 2 * hz; tick++)
        {
            bool moving = direction * Feet(s).X < direction * end;
            float before = Feet(s).Y;
            s = g.Step(s, moving ? direction * Vector2.UnitX : Vector2.Zero, moving ? 1 : 0, false, Tuning,
                1f / hz, run);
            float change = Feet(s).Y - before;
            string context = $"tick {tick}, feet {before:R} to {Feet(s).Y:R}, step {s.StepDeltaY:R}: {Show(s)}";
            Assert.True(s.Grounded, context);
            Assert.True(s.StepDeltaY == 0 || Math.Sign(s.StepDeltaY) == Math.Sign(change), context);
            if (!up) Assert.True(s.StepDeltaY <= 0, context);
            sum += s.StepDeltaY;
        }
        Assert.True(direction * Feet(s).X >= direction * end, Show(s));
        if (up) AssertNear(-EdgeDrop, sum, Skin, $"sum {sum:R}");
        else Assert.True(Math.Abs(sum) <= EdgeDrop + Skin, $"sum {sum:R}");
    }
}
