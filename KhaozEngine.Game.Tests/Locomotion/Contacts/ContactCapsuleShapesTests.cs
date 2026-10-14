using System;
using KhaozEngine.Locomotion;
using Xunit;

namespace KhaozEngine.Tests.Locomotion.Contacts;

public sealed class ContactCapsuleShapesTests
{
    [Fact]
    public void RolesAndExactDimensionBitsKeepIndependentImmutableShapes()
    {
        var shapes = new ContactCapsuleShapes();
        var body = shapes.Get(ContactCapsuleRole.Body, 0.4f, 1);
        var shell = shapes.Get(ContactCapsuleRole.Shell, 0.4f, 0.6f);
        var sweep = shapes.Get(ContactCapsuleRole.RoundedSweep, MathF.BitIncrement(0.4f), 1);
        var contact = shapes.Get(ContactCapsuleRole.RoundedContact, 0.401f, 1);
        Assert.Same(body, shapes.Get(ContactCapsuleRole.Body, 0.4f, 1));
        Assert.Same(shell, shapes.Get(ContactCapsuleRole.Shell, 0.4f, 0.6f));
        Assert.Same(sweep, shapes.Get(ContactCapsuleRole.RoundedSweep, MathF.BitIncrement(0.4f), 1));
        Assert.Same(contact, shapes.Get(ContactCapsuleRole.RoundedContact, 0.401f, 1));
        Assert.Equal(BitConverter.SingleToInt32Bits(0.4f), BitConverter.SingleToInt32Bits(body.Radius));
        Assert.Equal(BitConverter.SingleToInt32Bits(MathF.BitIncrement(0.4f)), BitConverter.SingleToInt32Bits(sweep.Radius));
        Assert.NotSame(shapes.Get(ContactCapsuleRole.Body, 0.4f, 0f),
            shapes.Get(ContactCapsuleRole.Body, 0.4f, -0f));
    }
}
