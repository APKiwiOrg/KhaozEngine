using System;
using KhaozEngine.Attachments;
using Xunit;

namespace KhaozEngine.Tests.Attachments;

internal static class AttachmentSetReferenceAssertions
{
    internal static void Joint(Action action, uint partId, string joint)
    {
        var error = Assert.Throws<AttachmentSetDataException>(action);
        Assert.IsAssignableFrom<MalformedDataException>(error);
        Assert.Equal(AttachmentSetRefusal.JointReferenceUnresolved, error.Reason);
        Assert.Equal(partId, error.PartId);
        Assert.Equal(joint, error.JointName);
    }

    internal static void Mesh(Action action, uint partId, string meshKey, string? actualHash)
    {
        var error = Assert.Throws<AttachmentSetDataException>(action);
        Assert.Equal(AttachmentSetRefusal.MeshReferenceMissingOrHashMismatch, error.Reason);
        Assert.Equal(partId, error.PartId);
        Assert.Equal(meshKey, error.MeshKey);
        Assert.Equal(AttachmentFixtures.Hash, error.ExpectedHash);
        Assert.Equal(actualHash, error.ActualHash);
    }
}
