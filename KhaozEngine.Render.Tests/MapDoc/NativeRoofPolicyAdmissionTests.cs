using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeRoofPolicyAdmissionTests
{
    [Fact]
    public void PolicyClosure_ReleasesSupersededScenesAndTheFinalOwner()
    {
        using var fixture = NativeSceneFixtures.Create("empty");
        var source = NativeSceneFixtures.Open(fixture, false);
        // Two empty snapshots, the 4096-byte acquisition workspace and bounded hashing fit 32 KiB.
        var admission = new MapSceneAdmission(new(MaxPreparedBytes: 32768));
        using var active = admission.Enter();
        var initial = MapSceneAcquisition.Run(source, NativePresentationFixtures.Scope, admission).Scene!;
        var final = MapRoofPolicyAcquisition.Complete(initial);
        Assert.True(final.Witness.CompleteForScope);
        Assert.NotSame(initial, final);
        Assert.Equal(0, initial.PreparationOwnership!.Bytes);
        Assert.Equal(final.PreparationOwnership!.Bytes, admission.RetainedBytes);
        Assert.Equal(0, admission.WorkspaceBytes);
        final.PreparationOwnership.Dispose();
        Assert.Equal(0, admission.RetainedBytes);
    }

}
