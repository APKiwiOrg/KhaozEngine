using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Covers the non-throwing decode path: <see cref="ClientReplicationView.TryApply"/> turns a snapshot referencing
/// an unregistered component type id (a newer server protocol) into a <c>false</c> + error instead of throwing -
/// the engine-level backstop for the "old client vs upgraded server" hard crash.
/// </summary>
public class ClientReplicationViewTryApplyTests
{
    private struct Pos : IComponent { public float X; public float Y; }

    private static ReplicationRegistry RegistryWithPos()
    {
        var r = new ReplicationRegistry();
        r.Register<Pos>(
            typeId: 1,
            write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
            read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });
        return r;
    }

    [Fact]
    public void TryApply_ReturnsTrue_OnAGoodSnapshot()
    {
        ReplicationRegistry registry = RegistryWithPos();
        var server = new World();
        Entity e = server.Spawn(); server.Set(e, new NetId(7)); server.Set(e, new Pos { X = 1, Y = 2 });
        byte[] snap = SnapshotWriter.Write(server, registry);

        var view = new ClientReplicationView(registry);
        bool ok = view.TryApply(new World(), snap, out string? error);

        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void TryApply_ReturnsFalse_WithoutThrowing_OnUnregisteredTypeId()
    {
        // Snapshot written with a registry the client lacks (id 1): the client decodes against an empty registry,
        // exactly the "server replicates a type the old client never registered" skew.
        ReplicationRegistry serverRegistry = RegistryWithPos();
        var server = new World();
        Entity e = server.Spawn(); server.Set(e, new NetId(7)); server.Set(e, new Pos { X = 1, Y = 2 });
        byte[] snap = SnapshotWriter.Write(server, serverRegistry);

        var clientView = new ClientReplicationView(new ReplicationRegistry());   // knows no types
        bool ok = clientView.TryApply(new World(), snap, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("unregistered type id", error);
    }
}
