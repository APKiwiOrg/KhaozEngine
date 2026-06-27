using System.Numerics;
using KhaozEngine.Collision;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class CharacterMovementCollisionTests
{
    static float Flat(float x, float z) => 0f;
    static MoveCommand Forward => new(new Vector2(0f, 1f), run: false, cameraYaw: 0f); // forward = -Z world

    [Fact]
    public void NoColliders_MovementUnchanged()
    {
        var tuning = MoveTuning.Default;
        Vector3 with = CharacterMovement.Step(Vector3.Zero, Forward, 1f, Flat, tuning, null, null);
        Vector3 without = CharacterMovement.Step(Vector3.Zero, Forward, 1f, Flat, tuning, null);
        Assert.Equal(without, with);
    }

    [Fact]
    public void WalkingIntoTree_IsPushedOut_CannotEnter()
    {
        var tuning = MoveTuning.Default; // CapsuleRadius 0.4, WalkSpeed 3
        // Tree (cylinder r=1) 1.5 ahead in -Z. Walk straight at it for many steps; combined radius 1.4.
        var tree = WorldCollider.Cylinder(new Vector2(0f, -1.5f), 1f);
        var set = new WorldColliders(new[] { tree });
        Vector3 p = Vector3.Zero;
        for (int i = 0; i < 60; i++)
            p = CharacterMovement.Step(p, Forward, 1f / 30f, Flat, tuning, null, set);
        float dist = new Vector2(p.X, p.Z + 1.5f).Length();
        Assert.True(dist >= 1.4f - 0.02f, $"penetrated tree: dist={dist}");
        Assert.True(p.Z > -0.2f, $"walked through tree to z={p.Z}");
    }

    [Fact]
    public void WalkingAlongWall_Slides()
    {
        var tuning = MoveTuning.Default;
        // A wall box centred at z=-1.0, wide in X, thin in Z (near face at z=-0.75). Moving diagonally
        // forward-and-right (+X, -Z): the -Z component is blocked by the wall, the +X component slides.
        var wall = WorldCollider.Box(new Vector2(0f, -1.0f), new Vector2(10f, 0.25f), yaw: 0f);
        var set = new WorldColliders(new[] { wall });
        var diagonal = new MoveCommand(new Vector2(1f, 1f), run: false, cameraYaw: 0f);
        Vector3 p = new(0f, 0f, -0.4f);
        float startX = p.X;
        for (int i = 0; i < 30; i++)
            p = CharacterMovement.Step(p, diagonal, 1f / 30f, Flat, tuning, null, set);
        Assert.True(p.X > startX + 0.5f, $"did not slide along wall: x={p.X}");
        Assert.True(p.Z > -0.8f, $"penetrated wall: z={p.Z}"); // kept out on the near (+Z) side of the wall
    }
}
