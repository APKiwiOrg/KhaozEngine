using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion;

public class CameraRelativeDirTests
{
    static readonly Func<float, float, float> FlatGround = (x, z) => 0f;
    static readonly MoveTuning Tuning = MoveTuning.Default with { CapsuleHalfHeight = 0f };

    static void AssertDir(Vector2 expected, Vector2 actual)
    {
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
    }

    [Fact]
    public void Idle_IsZero()
    {
        Assert.Equal(Vector2.Zero, CharacterMovement.CameraRelativeDir(new MoveCommand(Vector2.Zero, run: false, cameraYaw: 0f)));
        // Inside the 1e-6 length-squared dead-zone counts as idle too.
        Assert.Equal(Vector2.Zero, CharacterMovement.CameraRelativeDir(new MoveCommand(new Vector2(0.0005f, 0f), run: false, cameraYaw: 1.2f)));
    }

    [Fact]
    public void Basis_KnownYaws()
    {
        // yaw 0: camera looks down -Z, so forward = (0, -1), right = (1, 0).
        AssertDir(new Vector2(0f, -1f), CharacterMovement.CameraRelativeDir(new MoveCommand(new Vector2(0f, 1f), false, 0f)));   // forward
        AssertDir(new Vector2(1f, 0f), CharacterMovement.CameraRelativeDir(new MoveCommand(new Vector2(1f, 0f), false, 0f)));    // strafe right
        float inv = 1f / MathF.Sqrt(2f);
        AssertDir(new Vector2(inv, -inv), CharacterMovement.CameraRelativeDir(new MoveCommand(new Vector2(1f, 1f), false, 0f))); // forward-right normalized

        // yaw pi/2: forward rotates to (-1, 0), right rotates to (0, -1).
        AssertDir(new Vector2(-1f, 0f), CharacterMovement.CameraRelativeDir(new MoveCommand(new Vector2(0f, 1f), false, MathF.PI / 2f)));
        AssertDir(new Vector2(0f, -1f), CharacterMovement.CameraRelativeDir(new MoveCommand(new Vector2(1f, 0f), false, MathF.PI / 2f)));
    }

    [Fact]
    public void ResultIsUnit_WhenMoving()
    {
        for (float yaw = -3f; yaw <= 3f; yaw += 0.37f)
        {
            Vector2 d = CharacterMovement.CameraRelativeDir(new MoveCommand(new Vector2(0.4f, -0.9f), false, yaw));
            Assert.Equal(1f, d.Length(), 4);
        }
    }

    [Fact]
    public void PublicDir_EqualsWhatPredictionResolves()
    {
        // With flat ground and no slope gate, the horizontal step advances the position exactly along the resolved
        // direction (DesiredHorizontalCore: delta = moveDir * speed * dt). So the normalized XZ delta the step commits
        // must equal CameraRelativeDir(cmd) - the public path is the same direction prediction moves along.
        (Vector2 move, float yaw)[] cases =
        {
            (new Vector2(0f, 1f), 0f),
            (new Vector2(1f, 0f), 0f),
            (new Vector2(1f, 1f), 0f),
            (new Vector2(-0.6f, 0.3f), 1.1f),
            (new Vector2(0.2f, -0.8f), -2.4f),
            (new Vector2(0.9f, 0.9f), MathF.PI),
        };
        foreach ((Vector2 move, float yaw) in cases)
        {
            var cmd = new MoveCommand(move, run: false, cameraYaw: yaw);
            Vector2 dir = CharacterMovement.CameraRelativeDir(cmd);

            Vector3 start = Vector3.Zero;
            Vector3 end = CharacterMovement.Step(start, cmd, dt: 0.1f, FlatGround, Tuning);
            var delta = new Vector2(end.X - start.X, end.Z - start.Z);
            Assert.True(delta.Length() > 1e-4f, "expected the step to move for a non-idle command");
            Vector2 stepDir = Vector2.Normalize(delta);

            AssertDir(dir, stepDir);
        }
    }
}
