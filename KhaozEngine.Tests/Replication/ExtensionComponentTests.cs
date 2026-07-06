using System.Collections.Generic;
using System.IO;
using KhaozEngine.Ecs;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

/// <summary>
/// Covers the forward-compatible "extension component" seam: a consumer registers a component at a type id
/// at/above <see cref="ReplicationRegistry.FirstExtensionTypeId"/>, and such components are length-prefixed on
/// the wire so a client whose registry does NOT know that id can SKIP it (ignore) instead of disconnecting. Built-in
/// ids (below the floor) keep their exact, unprefixed encoding and still hard-fail on an unknown id (the existing
/// "client out of date" contract, see <see cref="ClientReplicationViewTryApplyTests"/>).
/// </summary>
public class ExtensionComponentTests
{
    private struct Pos : IComponent { public float X; public float Y; }         // id 1 (built-in range)
    private struct Tag : IComponent { public int Value; }                        // id 2 (built-in range)
    private struct Mystery : IComponent { public int A; public int B; public int C; } // id 16 (extension range)

    private static void RegisterPos(ReplicationRegistry r) => r.Register<Pos>(
        typeId: 1,
        write: (p, bw) => { bw.Write(p.X); bw.Write(p.Y); },
        read: br => new Pos { X = br.ReadSingle(), Y = br.ReadSingle() });

    private static void RegisterTag(ReplicationRegistry r) => r.Register<Tag>(
        typeId: 2,
        write: (t, bw) => bw.Write(t.Value),
        read: br => new Tag { Value = br.ReadInt32() });

    private static void RegisterMystery(ReplicationRegistry r) => r.Register<Mystery>(
        typeId: ReplicationRegistry.FirstExtensionTypeId,
        write: (m, bw) => { bw.Write(m.A); bw.Write(m.B); bw.Write(m.C); },
        read: br => new Mystery { A = br.ReadInt32(), B = br.ReadInt32(), C = br.ReadInt32() });

    // Server knows Pos + Mystery + Tag (Mystery registered BETWEEN the two built-ins so the wire carries a known
    // component AFTER the extension one, proving the reader realigns past a skipped extension).
    private static ReplicationRegistry FullRegistry()
    {
        var r = new ReplicationRegistry();
        RegisterPos(r); RegisterMystery(r); RegisterTag(r);
        return r;
    }

    // Old client: knows only the built-in Pos + Tag, never registered the extension id.
    private static ReplicationRegistry OldClientRegistry()
    {
        var r = new ReplicationRegistry();
        RegisterPos(r); RegisterTag(r);
        return r;
    }

    [Fact]
    public void ExtensionComponent_RoundTrips_WhenBothEndsKnowIt()
    {
        ReplicationRegistry registry = FullRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(42));
        server.Set(e, new Pos { X = 1, Y = 2 });
        server.Set(e, new Mystery { A = 7, B = 8, C = 9 });
        server.Set(e, new Tag { Value = 55 });

        byte[] snap = SnapshotWriter.WriteFiltered(server, registry, new HashSet<long> { 42 });

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.Apply(client, snap);

        Assert.True(view.TryGetEntity(42, out Entity c));
        Assert.Equal(1f, client.Get<Pos>(c).X);
        Assert.Equal(8, client.Get<Mystery>(c).B);
        Assert.Equal(55, client.Get<Tag>(c).Value);
    }

    [Fact]
    public void UnknownExtensionTypeId_IsIgnored_AndFollowingKnownComponentStillDecodes()
    {
        // Server sends Pos + Mystery(ext) + Tag; the old client lacks Mystery. It must skip Mystery via its length
        // prefix and still decode Tag (which follows it on the wire): no throw, no disconnect.
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(42));
        server.Set(e, new Pos { X = 3, Y = 4 });
        server.Set(e, new Mystery { A = 1, B = 2, C = 3 });
        server.Set(e, new Tag { Value = 99 });
        byte[] snap = SnapshotWriter.WriteFiltered(server, FullRegistry(), new HashSet<long> { 42 });

        var client = new World();
        var view = new ClientReplicationView(OldClientRegistry());
        bool ok = view.TryApply(client, snap, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(view.TryGetEntity(42, out Entity c));
        Assert.Equal(3f, client.Get<Pos>(c).X);       // built-in before the skipped extension
        Assert.Equal(99, client.Get<Tag>(c).Value);   // built-in AFTER it: realignment proven
        Assert.False(client.Has<Mystery>(c));          // the unknown extension was dropped, not decoded
    }

    [Fact]
    public void UnknownBuiltInTypeId_StillThrows_PreservingOutOfDateContract()
    {
        // A component registered BELOW the floor that the client doesn't know is a core-protocol mismatch, not a
        // skippable extension: it must still fail the apply (surfaced as "client out of date"), unchanged.
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(1));
        server.Set(e, new Tag { Value = 5 });          // id 2, below the floor
        byte[] snap = SnapshotWriter.WriteFiltered(server, FullRegistry(), new HashSet<long> { 1 });

        var clientView = new ClientReplicationView(new ReplicationRegistry());   // knows nothing (id 2 unknown)
        bool ok = clientView.TryApply(new World(), snap, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("unregistered type id", error);
    }

    [Fact]
    public void ExtensionComponent_RoundTrips_ThroughDelta()
    {
        ReplicationRegistry registry = FullRegistry();
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(7));
        server.Set(e, new Pos { X = 0, Y = 0 });
        server.Set(e, new Mystery { A = 10, B = 20, C = 30 });

        var repl = new ServerReplicator(registry);
        repl.Capture(server);

        var client = new World();
        var view = new ClientReplicationView(registry);
        view.ApplyDelta(client, repl.WriteFor(0));

        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(20, client.Get<Mystery>(c).B);
    }

    [Fact]
    public void UnknownExtensionTypeId_IsIgnored_ThroughDelta()
    {
        var server = new World();
        Entity e = server.Spawn();
        server.Set(e, new NetId(7));
        server.Set(e, new Pos { X = 1, Y = 1 });
        server.Set(e, new Mystery { A = 4, B = 5, C = 6 });
        server.Set(e, new Tag { Value = 77 });

        var repl = new ServerReplicator(FullRegistry());
        repl.Capture(server);
        byte[] delta = repl.WriteFor(0);

        var client = new World();
        var view = new ClientReplicationView(OldClientRegistry());
        bool ok = view.TryApplyDelta(client, delta, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(view.TryGetEntity(7, out Entity c));
        Assert.Equal(1f, client.Get<Pos>(c).X);
        Assert.Equal(77, client.Get<Tag>(c).Value);
        Assert.False(client.Has<Mystery>(c));
    }

    [Fact]
    public void MalformedExtensionLength_IsCaughtCleanly_NotThrown()
    {
        // A hostile/corrupt extension frame whose declared length runs past the buffer must become a caught error
        // (a clean disconnect at the WorldClient level), never an unbounded read or a backward seek.
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(1);                                        // entity count
            bw.Write(5);                                        // netId
            bw.Write(ReplicationRegistry.FirstExtensionTypeId); // extension type id (ushort)
            bw.Write7BitEncodedInt(9999);                       // claims 9999 payload bytes...
            bw.Write((byte)1);                                  // ...but only 1 follows
        }

        var view = new ClientReplicationView(FullRegistry());
        bool ok = view.TryApply(new World(), ms.ToArray(), out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }
}
