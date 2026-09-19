using System.Numerics;
using KhaozEngine.Game;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Showcase;
using Xunit;

namespace KhaozEngine.Tests.Showcase;

public sealed class Room3DTestbedTests
{
    [Fact]
    public void The_outdoor_environment_enables_shadows_and_a_moving_sky()
    {
        var post = new PixelPostProcessSettings();
        using var environment = new Room3DEnvironment(post);
        Assert.Equal(ShadowMode.ShadowMap, post.Quality.Shadows.Mode);
        Assert.True(post.Sky.Enabled);
        Assert.False(post.Starfield);
        Vector3 before = post.LightDirection;
        environment.Advance(Room3DEnvironment.DayLengthSeconds / 4f);
        Assert.NotEqual(before, post.LightDirection);
    }

    [Fact]
    public void Pause_and_starfield_controls_do_not_destroy_the_day_cycle()
    {
        var post = new PixelPostProcessSettings();
        using var environment = new Room3DEnvironment(post);
        environment.Paused = true;
        Vector3 before = post.LightDirection;
        environment.Advance(10f);
        Assert.Equal(before, post.LightDirection);
        environment.ToggleBackground();
        Assert.True(post.Starfield);
        Assert.False(post.Sky.Enabled);
        environment.ToggleBackground();
        Assert.False(post.Starfield);
        Assert.True(post.Sky.Enabled);
    }

    [Fact]
    public void Leaving_the_room_restores_the_shared_scene_environment()
    {
        var post = new PixelPostProcessSettings();
        var sky = post.Sky;
        post.LightColor = new Color(0.3f, 0.5f, 0.7f, 1f);
        post.LightDirection = Vector3.UnitX;
        var light = post.LightColor;
        using (var environment = new Room3DEnvironment(post)) environment.Advance(80f);
        Assert.Same(sky, post.Sky);
        Assert.Equal(light, post.LightColor);
        Assert.Equal(Vector3.UnitX, post.LightDirection);
        Assert.Equal(ShadowMode.Off, post.Quality.Shadows.Mode);
        Assert.True(post.Starfield);
    }

    [Fact]
    public void Stair_treads_are_contiguous_and_within_the_characters_step_height()
    {
        var steps = Room3DTestbed.CreateSteps(static (_, _) => 3f);
        Assert.True(steps.Count >= 6);
        float previousTop = 3f;
        float previousFarEdge = steps[0].Center.Z - steps[0].HalfExtents.Z;
        foreach (var step in steps)
        {
            float top = step.Center.Y + step.HalfExtents.Y;
            Assert.InRange(top - previousTop, 0.01f, new CharacterController3D().StepHeight);
            Assert.Equal(previousFarEdge, step.Center.Z - step.HalfExtents.Z, 4);
            Assert.Equal(3f, step.Center.Y - step.HalfExtents.Y, 4);
            Assert.Equal(step.Center, Vector3.Transform(Vector3.Zero, step.World));
            previousTop = top;
            previousFarEdge = step.Center.Z + step.HalfExtents.Z;
        }
    }

    [Fact]
    public void Authored_tree_line_straddles_the_near_cascade_handoff()
    {
        var camera = new Vector3(0f, 8f, 10f);
        var trees = Room3DTestbed.CreateTreeLine(camera, Vector3.Zero, 16f, static (_, _) => 2f);
        Assert.True(trees.Count >= 4);
        bool before = false, after = false;
        foreach (var tree in trees)
        {
            float depth = Vector3.Dot(new Vector3(tree.X, tree.Y, tree.Z) - camera,
                Vector3.Normalize(-camera));
            before |= depth < 16f;
            after |= depth > 16f;
            Assert.InRange(depth, 14f, 18f);
            Assert.Equal(2f, tree.Y);
        }
        Assert.True(before && after);
    }
}
