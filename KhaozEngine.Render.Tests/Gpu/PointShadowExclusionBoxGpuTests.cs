using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu;

/// <summary>
/// A WALL LANTERN, which is the arrangement <see cref="LightShadow.NearRadius"/> cannot serve. The clearance is a
/// SPHERE around the bulb, and a fixture mounted on a wall is not spherical: this lantern needs 45 cm to clear its
/// own wall plate while the wall it hangs on stands 23 cm away, so any radius big enough to clear the fixture
/// clears the wall with it and any radius that keeps the wall casting leaves the cap corners, the rod, the arm and
/// the plate writing into the map. Those parts then paint the floor and the wall with the fixture's own shadow.
/// <para>
/// <see cref="LightShadow.ExclusionMin"/> and <see cref="LightShadow.ExclusionMax"/> are the answer: the consumer
/// hands the light the fixture's own world bounds, everything inside that box stops writing, and the wall one
/// centimetre outside it keeps writing exactly as it did. Relative assertions and no goldens, exactly as
/// <see cref="PointShadowGpuTests"/>: every probe is compared against the same scene rendered with
/// <see cref="LightShadow.None"/>, or against another probe in the same picture.
/// </para>
/// </summary>
public sealed class PointShadowExclusionBoxGpuTests(PointShadowScene fixture) : IClassFixture<PointShadowScene>
{
    /// <summary>The flame, inside the fixture, and how far the lantern reaches.</summary>
    static readonly Vector3 Flame = new(0f, 2f, 0f);
    const float Radius = 12f;

    /// <summary>The near radius the consumer actually shipped, and the reason this feature exists: the whole
    /// fixture needs 45 cm of clearance and the wall stands at 23 cm, so the sphere was sized to keep the wall
    /// and the rest of the lantern casts inside it.</summary>
    const float ShippedNearRadius = 0.16f;

    /// <summary>
    /// The fixture's own world bounds with a centimetre of skin, which is what a consumer passes: the union of the
    /// five pieces <see cref="DrawLantern"/> draws is x within 0.18, y from 1.85 to 2.20 and z from -0.21 to 0.18.
    /// The skin keeps a face of the fixture off the box's own boundary, where a fragment's comparison would be a
    /// coin flip.
    /// </summary>
    static readonly Vector3 FixtureMin = new(-0.19f, 1.84f, -0.22f);
    static readonly Vector3 FixtureMax = new(0.19f, 2.21f, 0.19f);

    /// <summary>A box a long way from the light, for the case that pins a box nowhere near the fixture as costing
    /// the picture nothing at all.</summary>
    static readonly Vector3 FarMin = new(50f, 50f, 50f);
    static readonly Vector3 FarMax = new(51f, 51f, 51f);

    /// <summary>The wall's front face, ONE CENTIMETRE outside the box. That is the whole point of a box over a
    /// sphere: it touches the clearance and still casts.</summary>
    const float WallFaceZ = -0.23f;

    /// <summary>A blue wall, so a probe can say whether it landed on the wall or on the floor behind it: the floor
    /// is white and reads the same in red and blue, and the wall reads blue with no red at all.</summary>
    static readonly Color WallBlue = new(0f, 0f, 1f, 1f);

    /// <summary>
    /// Where the cap plate's own corners land on the floor. The plate sits 10 cm under the flame, so the light
    /// magnifies it by twenty: the part of it OUTSIDE the 16 cm sphere starts at 12.5 cm from the axis and the
    /// square runs out to 18 cm on its axes and 25.5 cm at its corners, which is a dark ring on the floor from
    /// about 2.1 m out to between 4 m and 5 m depending on the direction. Every probe here is inside that ring
    /// and clear of the wall.
    /// </summary>
    static IEnumerable<Vector3> CapCornerProbes()
    {
        yield return new Vector3(2.8f, 0f, 0f);
        yield return new Vector3(-2.8f, 0f, 0f);
        yield return new Vector3(0f, 0f, 2.8f);
        yield return new Vector3(2f, 0f, 2f);
        yield return new Vector3(-2f, 0f, 2f);
    }

    /// <summary>The floor the lantern should be lighting: a grid in front of the wall, out to where the light is
    /// grazing it, crossing the whole of the ring above and the lit hole inside it.</summary>
    static IEnumerable<Vector3> FloorGrid()
    {
        foreach (float z in new[] { 0.7f, 2.1f, 3.5f })
            foreach (float x in new[] { -4.2f, -2.8f, -1.4f, 0f, 1.4f, 2.8f, 4.2f })
                yield return new Vector3(x, 0f, z);
    }

    /// <summary>A point on the wall's front face beside the fixture, which the fixture's own fan falls on.</summary>
    static readonly Vector3 WallFace = new(0.9f, 2f, WallFaceZ);

    /// <summary>The floor behind the wall, and its mirror on the open side at the same distance from the
    /// light.</summary>
    static readonly Vector3 BehindTheWall = new(0f, 0f, -3f);
    static readonly Vector3 InTheOpen = new(0f, 0f, 3f);

    /// <summary>
    /// THE HAZARD, and it is expected to reproduce rather than to pass by accident. The consumer's shipped
    /// clearance is a sphere that stops well inside the fixture, so the cap plate's corners, the rod, the arm and
    /// the wall plate all still write into the map and the floor the lantern should be lighting carries the
    /// fixture's own shadow.
    /// </summary>
    [GpuFact]
    public void ASphereBigEnoughToKeepTheWallLeavesTheFixtureCastingOnTheFloor()
    {
        PointShadowScene.Shot sphere = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(130, ShippedNearRadius));
        });
        PointShadowScene.Shot plain = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
        });

        Assert.Equal(1, sphere.ShadowedLights);
        var worst = (Probe: Vector3.Zero, Delta: 0);
        foreach (Vector3 probe in CapCornerProbes())
        {
            Assert.True(plain.Red(probe) > 40,
                $"the unshadowed floor at {probe} reads {plain.Red(probe)}, so this probe is measuring "
                + "something other than lit floor");
            int delta = plain.Red(probe) - sphere.Red(probe);
            if (delta > worst.Delta) worst = (probe, delta);
        }

        Assert.True(worst.Delta >= 20,
            $"the worst floor probe is {worst.Probe}, {worst.Delta} levels darker than the same scene with no "
            + "map at all. A sphere small enough to keep the wall casting must leave the rest of the fixture "
            + "casting too, or this case is not reproducing the hazard the exclusion box exists for.");
    }

    /// <summary>
    /// THE FIX. The fixture's own bounds as a box, and no near radius at all: every part of the lantern stops
    /// writing, so the floor in front of the wall reads exactly as it does with no map at all, to within the three
    /// levels the acne case already allows a floor for shadowing itself. The wall face beside the fixture is held
    /// to the same three levels.
    /// </summary>
    [GpuFact]
    public void TheFixturesOwnBoundsAsABoxGiveTheLanternItsRoomBack()
    {
        PointShadowScene.Shot cleared = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(131, FixtureMin, FixtureMax));
        });
        PointShadowScene.Shot plain = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity, LightShadow.None);
        });

        Assert.Equal(1, cleared.ShadowedLights);
        var worst = (Probe: Vector3.Zero, Delta: 0);
        foreach (Vector3 probe in FloorGrid())
        {
            Assert.True(plain.Red(probe) > 30,
                $"the unshadowed floor at {probe} reads {plain.Red(probe)}, so this probe is measuring "
                + "something other than lit floor");
            int delta = Math.Abs(cleared.Red(probe) - plain.Red(probe));
            if (delta > worst.Delta) worst = (probe, delta);
        }

        Assert.True(worst.Delta <= 3,
            $"the floor at {worst.Probe} reads {cleared.Red(worst.Probe)} with the fixture boxed out against "
            + $"{plain.Red(worst.Probe)} with no map at all, a gap of {worst.Delta} levels. The lantern is still "
            + "shadowing itself.");

        // The wall face beside the fixture, which is the surface the fan fell on. The blue albedo is the control:
        // the floor is white, so a probe that slipped off the wall would read red as well as blue.
        Assert.True(cleared.Blue(WallFace) > 30 && cleared.Red(WallFace) <= 4,
            $"the wall probe reads blue {cleared.Blue(WallFace)} and red {cleared.Red(WallFace)}, so it is not "
            + "on the blue wall face at all");
        Assert.True(Math.Abs(cleared.Blue(WallFace) - plain.Blue(WallFace)) <= 3,
            $"the wall face beside the fixture reads {cleared.Blue(WallFace)} with the fixture boxed out against "
            + $"{plain.Blue(WallFace)} with no map at all");
    }

    /// <summary>
    /// A BOX IS NOT A WAY OF TURNING THE SHADOW OFF, and this is the case a sphere could not pass: the wall's
    /// front face stands one centimetre outside the box, it TOUCHES the clearance, and it still casts. The floor
    /// behind it is dark and the floor the same distance out on the open side is lit.
    /// </summary>
    [GpuFact]
    public void TheWallOneCentimetreOutsideTheBoxStillCasts()
    {
        PointShadowScene.Shot shot = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(132, FixtureMin, FixtureMax));
        });

        Assert.Equal(1, shot.ShadowedLights);
        Assert.True(shot.Red(InTheOpen) > 60,
            $"the open side reads {shot.Red(InTheOpen)}, so the box has not given the lantern its light back");
        Assert.True(shot.Red(BehindTheWall) <= shot.Red(InTheOpen) - 20,
            $"the floor behind the wall reads {shot.Red(BehindTheWall)} against the open side's "
            + $"{shot.Red(InTheOpen)}, so a box has switched the shadow off rather than cut the fixture out of "
            + "it. The wall is one centimetre outside the box and must cast exactly as it did.");
    }

    /// <summary>
    /// THE BOX IS PART OF THE CACHED ROW'S IDENTITY. It decides what is drawn into the map, so the same key with
    /// a new box is a new map: the row is dirtied, re-rendered on the frame the change arrives, and the picture
    /// moves with it. A cache comparing only the key, the position, the radius and the near radius would answer
    /// the second frame with the first frame's dark picture for ever.
    /// </summary>
    [GpuFact]
    public void ChangingTheBoxOnOneKeyRedrawsTheRowAndThePictureWithIt()
    {
        IReadOnlyList<PointShadowScene.Shot> shots = fixture.Many(2, (s, frame) =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                frame == 0
                    ? LightShadow.Static(133, ShippedNearRadius)
                    : LightShadow.Static(133, ShippedNearRadius).WithExclusionBox(FixtureMin, FixtureMax));
        });

        Assert.Equal(1, shots[0].Diagnostics.PointStaticRebuilds);
        Assert.Equal(1, shots[1].Diagnostics.PointStaticRebuilds);
        Assert.NotEqual(shots[0].Rgba, shots[1].Rgba);
        foreach (Vector3 probe in CapCornerProbes())
            Assert.True(shots[1].Red(probe) >= shots[0].Red(probe) + 20,
                $"the floor at {probe} reads {shots[1].Red(probe)} once the fixture is boxed out against "
                + $"{shots[0].Red(probe)} before it, so the cached row was not redrawn");
    }

    /// <summary>
    /// A BOX NOWHERE NEAR THE LIGHT COSTS THE PICTURE NOTHING. The same scene with a box fifty metres away is the
    /// same BYTES as the same scene with no box at all, which is what pins the empty case as free: nothing about
    /// the pass changes until a fragment actually lands inside a box.
    /// </summary>
    [GpuFact]
    public void ABoxFarFromTheLightRendersTheSameBytesAsNoBox()
    {
        byte[] far = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(134, FarMin, FarMax));
        }).Rgba;
        PointShadowScene.Shot none = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity, LightShadow.Static(134));
        });
        // The control: the same scene whose box DOES reach the fixture. Two identical pictures would say nothing
        // if every box rendered the same picture.
        byte[] cleared = fixture.One(s =>
        {
            s.DrawFloor();
            DrawLantern(s);
            DrawWall(s);
            s.AddLight(Flame, Color.White, Radius, PointShadowScene.Intensity,
                LightShadow.Static(134, FixtureMin, FixtureMax));
        }).Rgba;

        Assert.Equal(1, none.ShadowedLights);
        Assert.Equal(none.Rgba, far);
        Assert.NotEqual(none.Rgba, cleared);
    }

    /// <summary>
    /// The lantern, as five pieces on one wall bracket: the flame's own closed body, the cap plate under it, the
    /// rod above it, the arm running back to the wall and the plate it is mounted on. Only the body lies inside
    /// the consumer's 16 cm sphere, which is exactly why that sphere does not work.
    /// </summary>
    static void DrawLantern(PointShadowScene.Frame s)
    {
        s.Box(0.10f, 0.10f, 0.10f, Flame);                            // the closed body around the flame
        s.Box(0.36f, 0.02f, 0.36f, new Vector3(0f, 1.90f, 0f));       // the cap plate, 10 cm under the flame
        s.Box(0.04f, 0.16f, 0.04f, new Vector3(0f, 2.12f, 0f));       // the rod above it
        s.Box(0.04f, 0.04f, 0.16f, new Vector3(0f, 2f, -0.10f));      // the arm back to the wall
        s.Box(0.20f, 0.30f, 0.04f, new Vector3(0f, 2f, -0.19f));      // the wall plate it hangs on
    }

    /// <summary>The wall the lantern hangs on: four metres tall, six wide, its front face one centimetre outside
    /// the fixture's box and its own body entirely outside it.</summary>
    static void DrawWall(PointShadowScene.Frame s) =>
        s.Box(6f, 4f, 0.4f, new Vector3(0f, 2f, WallFaceZ - 0.2f), WallBlue);
}
