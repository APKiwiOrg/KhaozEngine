using System;
using System.Numerics;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

public sealed partial class ClothContactTests
{
    [Fact]
    public void Distant_parts_of_one_fold_clear_on_both_skins_without_launching()
    {
        float overlap = .0005f;
        Vector3[] rest =
        [
            new(0, 0, 0), new(.04f, 0, 0),
            new(0, .04f, 0), new(.04f, .04f, 0),
            new(0, .07f, .03f), new(.04f, .07f, .03f),
            new(0, .04f, 2 * Material.HalfThickness - overlap), new(.04f, .04f, 2 * Material.HalfThickness - overlap),
            new(0, 0, 2 * Material.HalfThickness - overlap), new(.04f, 0, 2 * Material.HalfThickness - overlap),
        ];
        Vector2[] uv = new Vector2[rest.Length];
        ClothTriangle[] triangles = new ClothTriangle[8];
        for (int i = 0; i < rest.Length; i++)
            uv[i] = new Vector2(.04f * (i % 2), .04f * (i / 2));
        for (int row = 0; row < 4; row++)
        {
            int node = row * 2;
            triangles[node] = new ClothTriangle(node, node + 1, node + 3);
            triangles[node + 1] = new ClothTriangle(node, node + 3, node + 2);
        }
        ClothRig rig = ClothRig.Create(rest, uv, triangles, [], [], Material);
        var contact = new ClothContact(rig, ClothWorkLimits.LayeredContact);
        var step = new ClothSubstep([], [], [], Seconds, Vector3.Zero);
        Vector3[] working = rest.AsSpan().ToArray();

        contact.Begin();
        for (int iteration = 0; iteration < 4; iteration++)
            Assert.Equal(ClothStepStatus.Accepted, contact.Project(rest, working, step));
        Vector3[] velocities = new Vector3[working.Length];
        for (int i = 0; i < working.Length; i++)
            velocities[i] = (working[i] - rest[i] - contact.PositionBias[i]) / Seconds;
        Assert.Equal(ClothStepStatus.Accepted, contact.Complete(rest, working, velocities, step));

        Assert.True(working[0].Z < 0);
        Assert.True(working[8].Z > rest[8].Z);
        Assert.True(DistanceToTriangle(working[8], working[0], working[1], working[3]) >= 2 * Material.HalfThickness);
        Assert.All(velocities, velocity => Assert.InRange(velocity.Length(), 0, .000001f));
    }
}
