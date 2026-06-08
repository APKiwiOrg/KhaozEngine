using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Pos : IComponent { public int X; }
file struct Tag : IComponent { }

public class ColumnTests
{
    [Fact]
    public void RegistryAssignsDenseIdsAndDetectsTags()
    {
        var reg = new ComponentRegistry();
        int pos = reg.Id<Pos>();
        int tag = reg.Id<Tag>();
        Assert.Equal(pos, reg.Id<Pos>());      // stable
        Assert.NotEqual(pos, tag);
        Assert.False(reg.IsTag(pos));
        Assert.True(reg.IsTag(tag));           // no fields => tag
    }

    [Fact]
    public void ColumnStoresGetsAndSwapRemoves()
    {
        var reg = new ComponentRegistry();
        var col = (Column<Pos>)reg.CreateColumn(reg.Id<Pos>());
        col.EnsureCapacity(3);
        col.Set(0, new Pos { X = 10 });
        col.Set(1, new Pos { X = 20 });
        col.Set(2, new Pos { X = 30 });
        col.Get(1).X = 99;                     // ref mutation
        Assert.Equal(99, col.Get(1).X);
        col.SwapRemove(0, 2);                  // move last (row2) into row0
        Assert.Equal(30, col.Get(0).X);
    }
}
