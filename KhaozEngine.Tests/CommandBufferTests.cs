using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Hp : IComponent { public int V; }
file struct Dead : IComponent { }

public class CommandBufferTests
{
    [Fact]
    public void StructuralChangesDuringIterationApplyOnPlayback()
    {
        var w = new World();
        for (int i = 0; i < 3; i++) { var e = w.Spawn(); w.Set(e, new Hp { V = i }); }   // 0,1,2

        var ecb = new EntityCommandBuffer();
        w.ForEach((Entity e, ref Hp h) => { if (h.V == 0) ecb.Despawn(e); else ecb.Set(e, new Dead()); });
        // nothing changed yet (deferred)
        int before = 0; w.ForEach((Entity e, ref Hp h) => before++);
        Assert.Equal(3, before);

        ecb.Playback(w);
        int hpCount = 0; w.ForEach((Entity e, ref Hp h) => hpCount++);
        Assert.Equal(2, hpCount);                        // the V==0 entity despawned
        int deadCount = 0; w.Query().With<Dead>().ForEach((Entity e, ref Hp h) => deadCount++);
        Assert.Equal(2, deadCount);
    }

    [Fact]
    public void CreatedEntitiesGetTheirComponentsOnPlayback()
    {
        var w = new World();
        var ecb = new EntityCommandBuffer();
        var tmp = ecb.Create();
        ecb.Set(tmp, new Hp { V = 7 });
        ecb.Playback(w);
        int seen = 0; int val = 0;
        w.ForEach((Entity e, ref Hp h) => { seen++; val = h.V; });
        Assert.Equal(1, seen);
        Assert.Equal(7, val);
    }
}
