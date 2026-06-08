using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public struct DefMark : IComponent { public int V; }

public class DeferTests
{
    [Fact]
    public void DeferRunsInterleavedInRecordOrder()
    {
        var w = new World();
        var e = w.Spawn();
        var log = new List<string>();
        var ecb = new EntityCommandBuffer();
        ecb.Set(e, new DefMark { V = 1 });
        ecb.Defer(_ => log.Add("A"));
        ecb.Defer(_ => log.Add("B"));
        ecb.Playback(w);
        Assert.Equal(new[] { "A", "B" }, log.ToArray());   // deferred actions ran in record order
        Assert.Equal(1, w.Get<DefMark>(e).V);              // structural op also applied
    }

    [Fact]
    public void DeferSeesEffectsOfEarlierCommands()
    {
        var w = new World();
        var e = w.Spawn();
        int seen = -1;
        var ecb = new EntityCommandBuffer();
        ecb.Set(e, new DefMark { V = 7 });                  // earlier structural op
        ecb.Defer(world => seen = world.Get<DefMark>(e).V); // reads it during playback
        ecb.Playback(w);
        Assert.Equal(7, seen);
    }
}
