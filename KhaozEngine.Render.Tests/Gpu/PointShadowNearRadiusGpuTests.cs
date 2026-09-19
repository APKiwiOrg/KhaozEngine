using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// A LIGHT INSIDE ITS OWN FIXTURE, end to end on a real device. Every earlier proof of this feature stood its
/// light in open space, and a placed lamp is never in open space: the flame sits inside a closed body, so the
/// nearest surface in every direction is a few centimetres of the lamp itself, every receiver past it compares as
/// occluded, and the lamp lights nothing at all.
/// <para>
/// <see cref="LightShadow.NearRadius"/> is the answer, and the cases below are the whole of it: the hazard with
/// no clearance, the room lit again with one, the wall past it still casting, and a changed clearance redrawing
/// the cached row. Relative assertions and no goldens, exactly as <see cref="PointShadowGpuTests"/>: every probe
/// is compared against the same scene rendered with <see cref="LightShadow.None"/>.
/// </para>
/// </summary>
public sealed class PointShadowNearRadiusGpuTests(PointShadowScene fixture) : IClassFixture<PointShadowScene>
{
    /// <summary>The flame, inside the fixture, and how far the lamp reaches.</summary>
    static readonly Vector3 Flame = new(0f, 2f, 0f);
    const float Radius = 12f;

    /// <summary>The clearance the cases ask for: past the 17.3 cm corner of the fixture around the flame and
    /// nowhere near the wall two metres away.</summary>
    const float Clearance = 0.25f;

    /// <summary>The floor the lamp should be lighting, out along both sides of it. They start 1.6 m out because
    /// the bracket below the flame is a real caster that really does shadow its own foot, and because the fixture
    /// itself projects into the picture near the middle of it.</summary>
    static IEnumerable<Vector3> FloorProbes()
    {
        foreach (float x in new[] { 1.6f, 2.8f, 4f, 5.2f })
        {
            yield return new Vector3(x, 0f, 0f);
            yield return new Vector3(-x, 0f, 0f);
        }
    }

    /// <summary>The probe behind the wall in the wall case, and its mirror on the open side.</summary>
    static readonly Vector3 Shadowed = new(5f, 0f, 0f);
    static readonly Vector3 Open = new(-5f, 0f, 0f);

    /// <summary>
    /// THE HAZARD ITSELF, and it is expected to reproduce rather than to pass by accident. A lamp with no
    /// clearance stores its own body at six to seventeen centimetres in every direction, so every one of the
    /// probes it should be lighting reads as fully occluded.
    /// </summary>
    [GpuFact]
    public void WithNoNearRadiusTheLampIsFullyShadowedByItsOwnFixture()
    {
        PointShadowScene.Shot shadowed = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawLampFixture(Flame);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(120));
        });
        PointShadowScene.Shot plain = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawLampFixture(Flame);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
        });

        Assert.Equal(1, shadowed.ShadowedLights);
        foreach (Vector3 probe in FloorProbes())
        {
            // The control, which also says the fixture's own silhouette is not sitting on the probe: an
            // unshadowed lamp lights this floor.
            Assert.True(plain.Red(probe) > 40,
                $"the unshadowed floor at {probe} reads {plain.Red(probe)}, so this probe is measuring "
                + "something other than lit floor");
            Assert.True(shadowed.Red(probe) <= plain.Red(probe) - 20,
                $"the floor at {probe} reads {shadowed.Red(probe)} against the unshadowed {plain.Red(probe)}: "
                + "a light inside closed geometry must be shadowed by it when nothing is cleared");
        }
    }

    /// <summary>
    /// THE FIX. A clearance past the fixture takes the lamp's own body out of its map and the room is lit exactly
    /// as it is with no map at all, to within the three levels the acne case already allows for the floor
    /// shadowing itself.
    /// <para>
    /// The lamp is on its bracket here rather than a bare cube on purpose. A bare cube lies wholly inside the
    /// clearance, so the CPU sphere test drops the instance and the fragment's discard is never asked anything,
    /// which would leave half the fix unproven. The bracket reaches well past the clearance, so this case can only
    /// pass if the FRAGMENT is throwing away the geometry near the flame.
    /// </para>
    /// </summary>
    [GpuFact]
    public void ANearRadiusPastTheFixtureGivesTheLampItsRoomBack()
    {
        PointShadowScene.Shot cleared = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawLampOnItsBracket(Flame);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(121, Clearance));
        });
        PointShadowScene.Shot plain = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawLampOnItsBracket(Flame);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
        });

        Assert.Equal(1, cleared.ShadowedLights);
        var worst = (Probe: Vector3.Zero, Delta: 0);
        foreach (Vector3 probe in FloorProbes())
        {
            Assert.True(plain.Red(probe) > 40,
                $"the unshadowed floor at {probe} reads {plain.Red(probe)}, so this probe is measuring "
                + "something other than lit floor");
            int delta = Math.Abs(cleared.Red(probe) - plain.Red(probe));
            if (delta > worst.Delta) worst = (probe, delta);
        }

        Assert.True(worst.Delta <= 3,
            $"the floor at {worst.Probe} reads {cleared.Red(worst.Probe)} with the fixture cleared against "
            + $"{plain.Red(worst.Probe)} with no map at all, a gap of {worst.Delta} levels. The lamp is still "
            + "shadowing itself.");
    }

    /// <summary>
    /// A CLEARANCE IS NOT A WAY OF TURNING THE SHADOW OFF. The same lamp with the usual wall two metres away: the
    /// floor behind the wall is still dark and the open side is still lit, so only the geometry inside the
    /// clearance stopped writing.
    /// </summary>
    [GpuFact]
    public void AClearedLampStillCastsEverythingPastTheClearance()
    {
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawLampOnItsBracket(Flame);
            s.DrawWall();
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(122, Clearance));
        });

        Assert.Equal(1, shot.ShadowedLights);
        Assert.True(shot.Red(Open) > 60,
            $"the open side reads {shot.Red(Open)}, so the clearance has not given the lamp its light back");
        Assert.True(shot.Red(Shadowed) <= shot.Red(Open) - 20,
            $"the floor behind the wall reads {shot.Red(Shadowed)} against the open side's {shot.Red(Open)}, "
            + "so a near radius has switched the shadow off rather than cut the fixture out of it");
    }

    /// <summary>
    /// THE CLEARANCE IS PART OF THE CACHED ROW'S IDENTITY. It decides what is drawn into the map, so the same key
    /// at a new clearance is a new map: the row is dirtied, re-rendered on the frame the change arrives, and the
    /// picture moves with it. A cache comparing only the key, the position and the radius would answer the second
    /// frame with the first frame's dark picture for ever.
    /// </summary>
    [GpuFact]
    public void ChangingTheNearRadiusOnOneKeyRedrawsTheRowAndThePictureWithIt()
    {
        IReadOnlyList<PointShadowScene.Shot> shots = fixture.Many(2, (s, frame) =>
        {
            s.DrawFloor();
            s.DrawLampOnItsBracket(Flame);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                frame == 0 ? LightShadow.Static(123) : LightShadow.Static(123, Clearance));
        });

        Assert.Equal(1, shots[0].Diagnostics.PointStaticRebuilds);
        Assert.Equal(1, shots[1].Diagnostics.PointStaticRebuilds);
        Assert.NotEqual(shots[0].Rgba, shots[1].Rgba);
        foreach (Vector3 probe in FloorProbes())
            Assert.True(shots[1].Red(probe) >= shots[0].Red(probe) + 20,
                $"the floor at {probe} reads {shots[1].Red(probe)} once the fixture is cleared against "
                + $"{shots[0].Red(probe)} before it, so the cached row was not redrawn");
    }

    /// <summary>
    /// A CASTER WHOLLY INSIDE THE CLEARANCE IS NOT DRAWN AT ALL. The fragment's discard would answer it anyway,
    /// but six faces of an instance that can contribute nothing is work the pass should not be doing, so the
    /// sphere test drops it on the CPU. Two spans (the floor and the fixture) across six faces is twelve caster
    /// draws, and one span is six.
    /// </summary>
    [GpuFact]
    public void AFixtureWhollyInsideTheClearanceIsNotEvenDrawn()
    {
        PointShadowScene.Shot kept = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawLampFixture(Flame);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(124));
        });
        PointShadowScene.Shot dropped = fixture.One(s =>
        {
            s.DrawFloor();
            s.DrawLampFixture(Flame);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(125, Clearance));
        });

        Assert.Equal(12, kept.Diagnostics.PointFaceDrawCalls);
        Assert.Equal(6, dropped.Diagnostics.PointFaceDrawCalls);
        foreach (Vector3 probe in FloorProbes())
            Assert.True(dropped.Red(probe) >= kept.Red(probe) + 20,
                $"the floor at {probe} reads {dropped.Red(probe)} with the fixture cleared against "
                + $"{kept.Red(probe)} with it kept");
    }
}
