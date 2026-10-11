using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public partial class ContactLeasedQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShellCylinderIsNotRoundedAgainByTheAdapter(bool sweep)
    {
        using var scene = new QueryScene();
        var shape = new CapsuleShape(0.251f, 0.7f);
        if (sweep) scene.Queries().SweepShell(shape, new(0, 0.9f, 0), Vector3.UnitX, 0.1f, out _, QueryFilter.StaticsOnly);
        else scene.Queries().PenetrationShell(shape, new(0, 0.9f, 0), out _);
        Assert.Equal(0.251f, scene.View.RawShape!.Radius);
        Assert.Equal(0.7f, scene.View.RawShape.Length);
    }

    [Fact]
    public void ShellContactProofIncludesTheMappedHeadUnderTheRoof()
    {
        using var geometry = FootSupportScenes.Floor(SceneVariant.Box);
        geometry.World.AddStatic(new BoxShape(new(4, 0.1f, 4)), Pose.At(new(0, 1.6005f, 0)));
        using var scene = new QueryScene(geometry);
        Span<CapsuleContact> contacts = stackalloc CapsuleContact[16];
        CapsuleContactResult proof = scene.Queries().QueryShellContacts(new(0.25f, 0.7f), new(0, 0.9f, 0), contacts);
        Assert.True(proof.Complete);
        Assert.True(proof.Written > 0);
        bool roof = false;
        foreach (CapsuleContact contact in contacts[..proof.Written])
            if (contact.Normal.Y < -0.99f)
            {
                Assert.InRange(contact.Separation, -0.00075f, -0.00025f);
                roof = true;
            }
        Assert.True(roof);
    }
}
