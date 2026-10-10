using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Support;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class SupportWorkTests
{
    // SupportWorkTests (MapEditor.Tests, internal work counters)
    [Fact]
    public void Support_ReadsOneQueryContextWithoutClones()
    {
        var room = BoundFaceContextFixtures.EightByEight();
        var work = new MapBoundFaceWork();
        var request = new MapSupportRequest(new MapFramePoint(WorldFrame.Origin, new(2.5f, 0.5f, 3.5f)), null, null, null, 1f, 1f, null);
        MapSupportResult r = new MapSupportQuery(room.View).Select(request, work);
        Assert.Equal((MapSupportStatus.Supported, 0f), (r.Status, r.WorldY));
        Assert.Equal((1, 2, 4L, 0), (work.ContextsPrepared, work.Compiles, work.CompiledFaces, room.View.PatchClones));   // membership shares the support context
    }
}
