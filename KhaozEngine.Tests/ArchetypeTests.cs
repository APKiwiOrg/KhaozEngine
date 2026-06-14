using KhaozEngine.Ecs;
using Xunit;

namespace KhaozEngine.Tests;

file struct A : IComponent { public int V; }

public class ArchetypeTests
{
    [Fact]
    public void SignatureEqualityAndHash()
    {
        var s1 = new ArchetypeSignature(new[] { 1, 3, 5 });
        var s2 = new ArchetypeSignature(new[] { 1, 3, 5 });
        var s3 = new ArchetypeSignature(new[] { 1, 3 });
        Assert.Equal(s1, s2);
        Assert.Equal(s1.GetHashCode(), s2.GetHashCode());
        Assert.NotEqual(s1, s3);
    }

    [Fact]
    public void AddRowAndSwapRemoveFixesEntities()
    {
        var reg = new ComponentRegistry();
        int a = reg.Id<A>();
        var arch = new Archetype(new[] { a }, reg);
        var e0 = new Entity(0, 1);
        var e1 = new Entity(1, 1);
        int r0 = arch.AddRow(e0);
        int r1 = arch.AddRow(e1);
        ((Column<A>)arch.Columns[a]).Set(r0, new A { V = 100 });
        ((Column<A>)arch.Columns[a]).Set(r1, new A { V = 200 });

        bool moved = arch.SwapRemove(r0, out Entity backfilled);
        Assert.True(moved);
        Assert.Equal(e1, backfilled);                          // e1 moved into row 0
        Assert.Equal(1, arch.Count);
        Assert.Equal(200, ((Column<A>)arch.Columns[a]).Get(0).V);
    }

    [Fact]
    public void TagComponentHasNoColumn()
    {
        var reg = new ComponentRegistry();
        int a = reg.Id<A>();
        int marker = reg.Id<MarkerC>();
        var arch = new Archetype(new[] { a, marker }, reg);
        Assert.True(arch.Columns.ContainsKey(a));
        Assert.False(arch.Columns.ContainsKey(marker));        // tag => no column
    }
}

file struct MarkerC : IComponent { }
