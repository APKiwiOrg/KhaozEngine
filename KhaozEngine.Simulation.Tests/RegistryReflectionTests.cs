using System;
using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct SerPos : IComponent { public int X; }
file struct SerTag : IComponent { }

public class RegistryReflectionTests
{
    [Fact]
    public void RegisterTypeMatchesGenericId()
    {
        var reg = new ComponentRegistry();
        int gen = reg.Id<SerPos>();
        int viaType = reg.RegisterType(typeof(SerPos));
        Assert.Equal(gen, viaType);                       // same type => same id
        Assert.Equal(typeof(SerPos), reg.TypeOf(gen));    // reverse lookup
        Assert.False(reg.IsTag(gen));
        Assert.True(reg.IsTag(reg.RegisterType(typeof(SerTag))));
    }

    [Fact]
    public void RegisterTypeRejectsNonComponentStructs()
    {
        var reg = new ComponentRegistry();
        Assert.Throws<ArgumentException>(() => reg.RegisterType(typeof(int)));
    }

    [Fact]
    public void ColumnBoxedRoundTrips()
    {
        var reg = new ComponentRegistry();
        var col = (Column<SerPos>)reg.CreateColumn(reg.RegisterType(typeof(SerPos)));
        col.EnsureCapacity(1);
        col.SetBoxed(0, new SerPos { X = 42 });
        Assert.Equal(42, ((SerPos)col.GetBoxed(0)).X);
    }
}
