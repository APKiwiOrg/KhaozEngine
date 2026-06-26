using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests.Ecs;

/// <summary>
/// The read/write access-declaration model (the vocabulary the system scheduler, layer 3, reuses). Two units may
/// run concurrently only if their declarations do not conflict.
/// </summary>
public class AccessSetTests
{
    private struct A : IComponent { }
    private struct B : IComponent { }
    private struct C : IComponent { }

    [Fact]
    public void WriteWrite_SameComponent_Conflicts()
    {
        AccessSet x = Access.Write<A>();
        AccessSet y = Access.Write<A>();
        Assert.True(x.ConflictsWith(y));
        Assert.True(y.ConflictsWith(x));
    }

    [Fact]
    public void WriteRead_SameComponent_Conflicts_BothDirections()
    {
        AccessSet writer = Access.Write<A>();
        AccessSet reader = Access.Read<A>();
        Assert.True(writer.ConflictsWith(reader));
        Assert.True(reader.ConflictsWith(writer));
    }

    [Fact]
    public void ReadRead_SameComponent_DoesNotConflict()
    {
        AccessSet x = Access.Read<A>();
        AccessSet y = Access.Read<A>();
        Assert.False(x.ConflictsWith(y));
    }

    [Fact]
    public void DisjointComponents_DoNotConflict()
    {
        AccessSet x = Access.Write<A>().Read<B>();
        AccessSet y = Access.Write<C>();
        Assert.False(x.ConflictsWith(y));
    }

    [Fact]
    public void None_NeverConflicts()
    {
        Assert.False(AccessSet.None.ConflictsWith(Access.Write<A>()));
        Assert.False(Access.Write<A>().Build().ConflictsWith(AccessSet.None));
        Assert.False(AccessSet.None.ConflictsWith(AccessSet.None));
    }

    [Fact]
    public void Write_BeatsRead_ForTheSameComponent()
    {
        AccessSet s = Access.Read<A>().Write<A>();   // declared both ⇒ classified as write only
        Assert.Contains(typeof(A), s.Writes);
        Assert.DoesNotContain(typeof(A), s.Reads);
    }
}
