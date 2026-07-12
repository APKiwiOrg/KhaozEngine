using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct Pos : IComponent { public int X; }
file struct Tag : IComponent { }
file struct ByteFlag : IComponent { public byte V; }   // one 1-byte field: NOT a tag, despite sizeof == 1

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

    // Locks the field-count classification the AOT trim suppression on ComponentRegistry.IsTagType relies on: a
    // single-byte struct has sizeof == 1 just like a zero-field tag, so any size-based shortcut would misclassify it.
    // It must stay a real (stored) component, so this guards against the classification silently degrading to a size
    // check under a future AOT-motivated rewrite.
    [Fact]
    public void SingleByteFieldStructIsNotATag()
    {
        var reg = new ComponentRegistry();
        int flag = reg.Id<ByteFlag>();
        Assert.False(reg.IsTag(flag));         // has a field => stored, not a tag

        var col = (Column<ByteFlag>)reg.CreateColumn(flag);
        col.EnsureCapacity(1);
        col.Set(0, new ByteFlag { V = 42 });
        Assert.Equal(42, col.Get(0).V);        // and its value round-trips through the column
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
