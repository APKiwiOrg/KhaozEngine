using System.Linq;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

public readonly record struct PlayerDamaged(float Amount);
public readonly record struct XpGranted(int Amount);

public class EventChannelTests
{
    [Fact]
    public void EventsReadBackInEmissionOrderPerType()
    {
        var w = new World();
        w.Emit(new PlayerDamaged(5));
        w.Emit(new XpGranted(10));
        w.Emit(new PlayerDamaged(3));
        Assert.Equal(new[] { 5f, 3f }, w.Events<PlayerDamaged>().Select(e => e.Amount).ToArray());
        Assert.Equal(new[] { 10 }, w.Events<XpGranted>().Select(e => e.Amount).ToArray());
        Assert.Empty(w.Events<int>());                       // unseen type -> empty
    }

    [Fact]
    public void AdvanceTickClearsEvents()
    {
        var w = new World();
        w.Emit(new XpGranted(1));
        w.AdvanceTick();
        Assert.Empty(w.Events<XpGranted>());
    }
}
