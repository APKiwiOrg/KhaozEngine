// Rows for the play-area bound against statics: a shell slide that would carry a substep past the bound, and the
// backstop after the move, reached only through a recovery push that crosses the bound. Expectations come from the
// installed geometry and the closed forms.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Locomotion.Contacts;
using KhaozEngine.Physics;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Contacts.FootSupportScenes;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactControllerTests
{
    // The bound of the rows below: z at 0 or more.
    static Vector2 KeepZ(float x, float z) => new(x, MathF.Max(z, 0));

    // A vertical wall whose face stands on x + z = 2, facing -X -Z, from Y -1 to 5 and 4 m either side of (2, 0). The
    // box variant is 0.3 deep behind the face. The mesh variant is the face alone.
    static FootSupportScene DiagonalWall(SceneVariant variant)
    {
        var scene = new FootSupportScene(variant);
        Vector3 outward = Vector3.Normalize(new Vector3(-1, 0, -1)), along = Vector3.Normalize(new Vector3(1, 0, -1));
        var centre = new Vector3(2, 2, 0);
        if (variant == SceneVariant.Box)
        {
            const float Thickness = 0.3f;
            Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 4);
            return scene.Add("wall", new BoxShape(new Vector3(Thickness / 2, 3, 4)),
                new Pose(centre - outward * (Thickness / 2), turn));
        }
        Vector3 a = centre - 4 * along - 3 * Vector3.UnitY, b = centre + 4 * along - 3 * Vector3.UnitY;
        Vector3 c = centre - 4 * along + 3 * Vector3.UnitY, d = centre + 4 * along + 3 * Vector3.UnitY;
        return scene.Mesh("wall",
            [.. FootSupportScene.Facing(a, b, c, outward), .. FootSupportScene.Facing(b, d, c, outward)]);
    }

    // Level terrain inside the bound, rising 1 m per metre of -z outside it.
    static float RisingOutside(float x, float z) => z < 0 ? -z : 0;

    static Vector3 RisingOutsideNormal(float x, float z) =>
        z < 0 ? Vector3.Normalize(new Vector3(0, 1, 1)) : Vector3.UnitY;

    // A body walks or runs +X along z 0 into the diagonal wall at the z >= 0 bound. The wall faces -X -Z, so the shell's
    // slide along it carries the move toward -Z, out of the bound, where the terrain rises. Each substep's planned move
    // is inside the bound, and the shell's slide is cut to the bound after every contact, so the body stops in the
    // corner of the wall and the bound as in any inner corner. Shifting a crossed end back after the move would put the
    // shell into the wall, and a placement it clears only by a push would keep the feet at the height they had on the
    // rising terrain. For 300 ticks every tick ends inside the bound, grounded and on the level terrain to half a skin.
    [Theory]
    [InlineData("box", 30, false)]
    [InlineData("box", 30, true)]
    [InlineData("box", 60, false)]
    [InlineData("box", 60, true)]
    [InlineData("mesh", 30, false)]
    [InlineData("mesh", 30, true)]
    [InlineData("mesh", 60, false)]
    [InlineData("mesh", 60, true)]
    public void SlideAlongAWallAcrossAClampBoundStaysInside(string ground, int hz, bool run)
    {
        using var g = new Ground(DiagonalWall(ground == "box" ? SceneVariant.Box : SceneVariant.Mesh), RisingOutside,
            RisingOutsideNormal);
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(0.8f, 0, 0));
        for (int tick = 1; tick <= 300; tick++)
        {
            s = g.Step(s, Vector2.UnitX, 1, false, Tuning, dt, run, clampXz: KeepZ);
            Vector3 feet = Feet(s);
            Assert.True(feet.Z >= 0, $"tick {tick} ended past the bound: {Show(s)}");
            Assert.True(s.Grounded, $"tick {tick}: {Show(s)}");
            AssertNear(RisingOutside(feet.X, feet.Z), feet.Y, HalfSkin, $"tick {tick} left the terrain: {Show(s)}");
        }
    }

    // A body that meets the diagonal wall inside the bound slides along it toward -Z. The slide that reaches the bound is
    // cut along its own direction where it meets it, so the body reaches the corner of the wall and the bound as it
    // reaches the corner of two walls. Clamping the slide's end onto the bound would turn it into the wall and lose the
    // whole slide, which held the body a few centimetres up the wall.
    [Theory]
    [InlineData("box", 30, false, 0.02f)]
    [InlineData("box", 30, true, 0.1f)]
    [InlineData("box", 60, true, 0.2f)]
    [InlineData("mesh", 30, false, 0.02f)]
    [InlineData("mesh", 30, true, 0.1f)]
    [InlineData("mesh", 60, true, 0.2f)]
    public void SlideAlongAWallReachesTheCornerWithTheBound(string ground, int hz, bool run, float z0)
    {
        using var g = new Ground(DiagonalWall(ground == "box" ? SceneVariant.Box : SceneVariant.Mesh), RisingOutside,
            RisingOutsideNormal);
        float dt = 1f / hz;
        MoveState s = Standing(new Vector3(0.8f, 0, z0));
        for (int tick = 1; tick <= 300; tick++)
            s = g.Step(s, Vector2.UnitX, 1, false, Tuning, dt, run, clampXz: KeepZ);
        Vector3 feet = Feet(s);
        Assert.True(feet.Z >= 0 && feet.Z <= Skin, $"held short of the corner: {Show(s)}");
    }

    // The ground core alone, from feet 0.46 off the diagonal wall at z 0, walking a 30 Hz tick +X. The sweep meets the
    // wall after about 0.08 and slides the rest along it toward -Z. The slide is cut to the bound, so the move ends
    // inside it, blocked.
    [Theory]
    [MemberData(nameof(Physical))]
    public void GroundCoreSlideAlongAWallStaysInsideTheClamp(string ground)
    {
        using var g = new Ground(DiagonalWall(ground == "box" ? SceneVariant.Box : SceneVariant.Mesh), RisingOutside,
            RisingOutsideNormal);
        GroundStepResult step = GroundCore.Step(new Vector3(1.35f, 0, 0), new Vector2(Tuning.WalkSpeed * Dt, 0), Dt,
            Tuning, Settings, g.Height, g.Normal, g.World, g.Lease, climbBudget: null, clampXz: KeepZ);
        Assert.True(step.Feet.Z >= 0, $"ended past the bound: {step}");
        Assert.True(step.Feet.X > 1.35f, $"never reached the wall: {step}");
        Assert.True(step.Blocked, $"not blocked: {step}");
    }

    // The air pass alone, from feet 0.5 up and 0.46 off the diagonal wall at z 0, at run speed +X for a 30 Hz tick.
    // The flight meets the wall and slides along it toward -Z. The slide is cut to the bound, so the flight ends inside
    // it.
    [Theory]
    [MemberData(nameof(Physical))]
    public void AirPassSlideAlongAWallStaysInsideTheClamp(string ground)
    {
        using var g = new Ground(DiagonalWall(ground == "box" ? SceneVariant.Box : SceneVariant.Mesh), RisingOutside,
            RisingOutsideNormal);
        AirStepResult air = AirPass.Step(new Vector3(1.35f, 0.5f, 0), new Vector2(Tuning.RunSpeed, 0), 0, Dt, Gravity,
            Tuning, Settings, Tuning.MaxSlopeRadians, g.Height, g.Normal, g.World, g.Lease, clampXz: KeepZ);
        Assert.True(air.Feet.Z >= 0, $"ended past the bound: {air}");
        Assert.True(air.Feet.X > 1.35f, $"never reached the wall: {air}");
    }

    // On level terrain, a 0.3 plank over z from 0.185 inside the z >= 0 bound, and a 0.55 block over z from 0.25 on it.
    // A body stands at the origin with the bottom of its shell, at feet + StepHeight, over the block's edge. Recovery
    // pushes it out across the bound and up, about 0.034 each way, where its foot disc no longer reaches the plank,
    // so the ground core holds it there. The backstop shifts the end back to z 0, where the foot disc reaches the plank
    // 0.27 above the feet, more than the 30 Hz climb budget, and the raised shell clears the block. The seat pays the
    // budget and no more.
    [Fact]
    public void BackstopPacesASeatOntoHigherSupport()
    {
        using var g = new Ground(new FootSupportScene(SceneVariant.Box).Flat("plank", -2, 2, 0.185f, 3, 0.3f)
            .Flat("block", -2, 2, 0.25f, 3, 0.55f), Flat);
        Vector3 recovered = ShellMotion.Recover(g.World, Vector3.Zero, Tuning, out bool cleared);
        Assert.True(cleared && recovered.Z < 0 && recovered.Z + FootRadiusOf(Tuning) < 0.185f,
            $"recovery did not cross the bound clear of the plank: {recovered}");
        double budget = (double)Tuning.MaxStepClimbSpeed * Dt;

        MoveState s = g.Step(Standing(Vector3.Zero), Vector2.Zero, 0, false, Tuning, clampXz: KeepZ);

        Assert.True(Feet(s).Z == 0, $"the backstop did not shift the end: {Show(s)}");
        AssertNear(recovered.Y + budget, Feet(s).Y, HalfSkin, $"the seat did not pay exactly the budget: {Show(s)}");
    }

    // A wall slab over z from 0.3, and a 50 degree face rising to +Z from (z 0.12, y 1.2) into it, over terrain far
    // below. A falling body starts at feet 1 on z 0 with its shell 0.1 into the wall. Recovery pushes it straight out
    // across the bound, where its foot disc no longer reaches the face, and the tick falls on beside the wall. The
    // backstop shifts the end back to z 0, where the face's plane stands about 0.09 above the feet, inside the reach
    // of the foot disc. An airborne end is never raised onto steep support, so the feet end no higher than recovery
    // left them.
    [Fact]
    public void BackstopNeverRaisesAnAirborneEndOntoSteepSupport()
    {
        const float Toe = 0.12f, ToeY = 1.2f, Half = 0.15f;
        float angle = Radians(50);
        // A slab rising to +X, turned a quarter about Y so it rises to +Z.
        Quaternion tilt = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 2) *
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
        Vector3 top = new(0, ToeY + Half * MathF.Sin(angle), Toe + Half * MathF.Cos(angle));
        Vector3 centre = top + Vector3.Transform(new Vector3(0, -0.1f, 0), tilt);
        using var g = new Ground(new FootSupportScene(SceneVariant.Box)
            .Slab("wall", new Vector3(0, 3, 1.3f), 0, 2, 1, 3)
            .Add("face", new BoxShape(new Vector3(Half, 0.1f, 2)), new Pose(centre, tilt)), Abyss);
        var start = new Vector3(0, 1, 0);
        Vector3 recovered = ShellMotion.Recover(g.World, start, Tuning, out bool cleared);
        Assert.True(cleared && recovered.Z + FootRadiusOf(Tuning) < Toe,
            $"recovery did not cross the bound clear of the face: {recovered}");

        MoveState s = g.Step(Falling(start), Vector2.Zero, 0, false, Tuning, clampXz: KeepZ);

        Assert.True(Feet(s).Z == 0, $"the backstop did not shift the end: {Show(s)}");
        Assert.False(s.Grounded, Show(s));
        Assert.True(Feet(s).Y <= recovered.Y + HalfSkin, $"the shift raised an airborne end: {Show(s)}");
    }

    // Level terrain with a 1 m terrain ridge along the z 0 bound, 0.1 wide, and a wall slab over z from 0.3 inside the
    // bound. A body stands at z 0.1 on the terrain with its shell 0.2 into the wall. Recovery pushes it straight out
    // across the bound, past the ridge, and the ground core holds it there on the level terrain. The backstop shifts
    // the end back to z 0, onto the ridge, more than StepHeight above the feet where no support reaches. The feet are
    // floored on the terrain there.
    [Fact]
    public void BackstopFloorsAShiftOntoRisingTerrain()
    {
        const float Ridge = 1;
        static float Height(float x, float z) => MathF.Abs(z) <= 0.05f ? Ridge : 0;
        using var g = new Ground(new FootSupportScene(SceneVariant.Box)
            .Slab("wall", new Vector3(0, 3, 1.3f), 0, 2, 1, 3), Height);
        Vector3 start = new(0, 0, 0.1f);
        Vector3 recovered = ShellMotion.Recover(g.World, start, Tuning, out bool cleared);
        Assert.True(cleared && recovered.Z < -0.05f, $"recovery did not cross the ridge: {recovered}");

        MoveState s = g.Step(Standing(start), Vector2.Zero, 0, false, Tuning, clampXz: KeepZ);

        Assert.True(Feet(s).Z == 0, $"the backstop did not shift the end: {Show(s)}");
        AssertNear(Ridge, Feet(s).Y, HalfSkin, $"the shift was not floored on the terrain: {Show(s)}");
    }
}
