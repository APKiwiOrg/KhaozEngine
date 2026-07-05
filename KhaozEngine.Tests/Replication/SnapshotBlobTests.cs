using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Replication;
using Xunit;

namespace KhaozEngine.Tests.Replication;

public class SnapshotBlobTests
{
    // Builds a persist snapshot blob by hand: [count][per entity: netId + (typeId,[len],payload).. + 0].
    // Extension ids (>= FirstExtensionTypeId) are length-prefixed; built-ins are unframed.
    private static byte[] Build(params (int netId, (ushort typeId, byte[] payload)[] comps)[] entities)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(entities.Length);
        foreach ((int netId, (ushort typeId, byte[] payload)[] comps) in entities)
        {
            bw.Write(netId);
            foreach ((ushort typeId, byte[] payload) in comps)
            {
                bw.Write(typeId);
                if (ReplicationRegistry.IsExtension(typeId)) bw.Write7BitEncodedInt(payload.Length);
                bw.Write(payload);
            }
            bw.Write((ushort)0);
        }
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public void Reader_ParsesExtensionFrames_NoResolverNeeded()
    {
        byte[] blob = Build(
            (7, new (ushort, byte[])[] { (16, new byte[] { 1, 2, 3 }), (17, new byte[] { 9 }) }),
            (8, new (ushort, byte[])[] { (16, new byte[] { 4, 5 }) }));

        var reader = new SnapshotBlobReader(blob);

        Assert.Equal(2, reader.Entities.Count);
        Assert.Equal(7, reader.Entities[0].NetId);
        Assert.Equal(2, reader.Entities[0].Components.Count);
        Assert.Equal((ushort)16, reader.Entities[0].Components[0].TypeId);
        Assert.Equal(new byte[] { 1, 2, 3 }, reader.Entities[0].Components[0].Payload);
        Assert.True(reader.Entities[0].Components[0].IsExtension);
        Assert.Equal(8, reader.Entities[1].NetId);
        Assert.Equal(new byte[] { 4, 5 }, reader.Entities[1].Components[0].Payload);
    }

    [Fact]
    public void ReaderThenWriter_RoundTripsByteIdentically()
    {
        byte[] blob = Build(
            (7, new (ushort, byte[])[] { (16, new byte[] { 1, 2, 3 }), (18, new byte[] { 42 }) }),
            (99, new (ushort, byte[])[] { (17, new byte[] { 7, 7 }) }));

        var reader = new SnapshotBlobReader(blob);
        var writer = new SnapshotBlobWriter();
        foreach (SnapshotBlobEntity e in reader.Entities) writer.AddEntity(e.NetId, e.Components);

        Assert.Equal(blob, writer.ToArray());
    }

    [Fact]
    public void Reader_BuiltinFrame_NeedsLengthResolver()
    {
        // A built-in component (id 1) with a 4-byte payload, unframed on the wire.
        byte[] blob = Build((5, new (ushort, byte[])[] { ((ushort)1, new byte[] { 0, 0, 0, 42 }) }));

        // Without a resolver the reader cannot know where the built-in payload ends.
        Assert.Throws<InvalidOperationException>(() => new SnapshotBlobReader(blob));

        // With the old-layout length it walks the frame.
        var reader = new SnapshotBlobReader(blob, builtinPayloadLength: id => id == 1 ? 4 : -1);
        Assert.Single(reader.Entities);
        Assert.Equal(new byte[] { 0, 0, 0, 42 }, reader.Entities[0].Components[0].Payload);
        Assert.False(reader.Entities[0].Components[0].IsExtension);
    }

    [Fact]
    public void Reader_CorruptExtensionLength_Throws()
    {
        // Extension frame claiming a payload longer than the buffer.
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(1);            // count
        bw.Write(3);            // netId
        bw.Write((ushort)16);   // extension type id
        bw.Write7BitEncodedInt(9999); // bogus length, far past the buffer
        bw.Write(new byte[] { 1, 2 });
        bw.Flush();

        Assert.Throws<InvalidOperationException>(() => new SnapshotBlobReader(ms.ToArray()));
    }
}
