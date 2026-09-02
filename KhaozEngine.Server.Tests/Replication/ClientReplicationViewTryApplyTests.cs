using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Covers the non-throwing decode path: <see cref="ClientReplicationView.TryApply"/> turns a snapshot referencing
/// an unregistered component type id (a newer server protocol) into a <c>false</c> + error instead of throwing -
/// the engine-level backstop for the "old client vs upgraded server" hard crash. Also pins what an ABORTED apply
/// leaves behind: the framed window is released on the way out, so the failed snapshot's buffer is not held alive.
/// </summary>
public class ClientReplicationViewTryApplyTests
{
    private struct Pos : IComponent { public float X; public float Y; }
    private struct Ext : IComponent { public int A; public int B; public int C; }   // extension range, length-prefixed

    private static void RegisterPos(ReplicationRegistry r) => r.Register<Pos>(
        typeId: 1,
        write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
        read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });

    private static void RegisterExt(ReplicationRegistry r) => r.Register<Ext>(
        typeId: ReplicationRegistry.FirstExtensionTypeId,
        write: (x, bw) => { bw.Write(x.A); bw.Write(x.B); bw.Write(x.C); },
        read: br => new Ext { A = br.ReadInt32(), B = br.ReadInt32(), C = br.ReadInt32() });

    private static ReplicationRegistry RegistryWithPos()
    {
        var r = new ReplicationRegistry();
        RegisterPos(r);
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

    [Fact]
    public void TryApply_ReleasesTheFramedWindow_WhenTheComponentLoopThrows()
    {
        // The extension component is registered FIRST so it rides ahead of the built-in on the wire, and the client
        // knows only that one. The framed window is therefore pointed at the extension's payload and the very next
        // type id throws, which is the shape every abort of this loop has. With the Release() sitting after the loop
        // rather than in a finally, the view walked out still holding the failed snapshot's byte[].
        var serverRegistry = new ReplicationRegistry();
        RegisterExt(serverRegistry);
        RegisterPos(serverRegistry);
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(7));
        server.Set(e, new Ext { A = 1, B = 2, C = 3 });
        server.Set(e, new Pos { X = 1, Y = 2 });
        byte[] snap = SnapshotWriter.Write(server, serverRegistry);

        var clientRegistry = new ReplicationRegistry();
        RegisterExt(clientRegistry);   // knows the extension, never registered Pos
        var view = new ClientReplicationView(clientRegistry);

        bool ok = view.TryApply(new World(), snap, out string? error);

        Assert.False(ok);
        Assert.Contains("unregistered type id", error);
        Assert.Equal(0L, view.FramedWindowLength);
    }
}
