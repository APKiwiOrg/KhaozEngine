using System;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld;

public sealed class TileWorldSnapshotSequenceTests
{
    [Fact]
    public void Coincident_initial_camera_is_rejected_before_scene_configuration()
    {
        bool configured = false;
        ArgumentException error = Assert.Throws<ArgumentException>(() => TileWorldSnapshot.CapturePerspectiveSequence(
            new TileWorldDocument(), TileRenderTestData.Catalogs, new GreyboxMeshResolver(1f, 3f),
            Vector3.Zero, Vector3.Zero, 80, 60, 2, (_, _) => { },
            configureScene: _ => configured = true));
        Assert.Equal("target", error.ParamName);
        Assert.False(configured);
    }
}
