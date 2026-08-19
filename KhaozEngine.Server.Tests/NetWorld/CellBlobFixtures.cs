using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using KhaozEngine.NetWorld;
using KhaozEngine.Primitives;

namespace KhaozEngine.Tests.NetWorld;

/// <summary>
/// Builds cell-blob snapshot bodies the way a build at a GIVEN wire generation would have written them, which is the
/// only way to test the migration chain honestly: <see cref="MoveProtocol.CreateRegistry"/> keeps just the current
/// encoder, so a body at generation 5 cannot be produced by the shipped codec at all.
/// <para>
/// The per-generation encoders here are written out field by field from the codec's own history (the generation notes
/// on <see cref="MoveProtocol.WireProtocolVersion"/>). <c>BuiltinBlobLayoutTests</c> is what stops them drifting: it
/// compares this ladder's newest rung byte-for-byte against the live codec and checks each rung is a prefix of the
/// next, so a codec change that is not mirrored here goes red rather than quietly baking a wrong fixture into every
/// migration test.
/// </para>
/// </summary>
internal static class CellBlobFixtures
{
    /// <summary>
    /// What the movement codec wrote at <paramref name="generation"/>: the same fields in the same order, stopping
    /// after the last one that generation had. Each line below is one wire generation's addition.
    /// </summary>
    internal static byte[] Movement(int generation, MovementState m)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(m.VerticalVelocity);                            // generation 1
        bw.Write(m.Grounded);                                    // generation 1
        bw.Write(m.TimeSinceGrounded);                           // generation 1
        bw.Write(m.JumpBufferRemaining);                         // generation 1
        if (generation >= 3) bw.Write(m.Swimming);               // generation 3: the surface-swim flag
        if (generation >= 4) bw.Write(m.TeleportEpoch);          // generation 4: the authoritative teleport epoch
        if (generation >= 5) bw.Write(m.ClimbRateQ);             // generation 5: the quantized step-climb rate
        if (generation >= 6) bw.Write(m.SpeedScaleQ);            // generation 6: the quantized haste/slow multiplier
        if (generation >= 7) bw.Write(m.HorizontalVelocityXQ);   // generation 7: carried airborne velocity, world X
        if (generation >= 7) bw.Write(m.HorizontalVelocityZQ);   // generation 7: carried airborne velocity, world Z
        if (generation >= 10) bw.Write(m.FacingYawQ);            // generation 10: the carried heading
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// What the position codec wrote at <paramref name="generation"/> for an ABSOLUTE world position: three float32s
    /// before generation <see cref="BuiltinBlobLayout.FramedPositionWireGeneration"/>, and the island-frame stamp
    /// plus the frame-local offset from it on. A framed fixture is written in
    /// <see cref="WorldFrame.Origin"/> so the two forms denote the identical position and a migration between them
    /// is checkable byte for byte.
    /// </summary>
    internal static byte[] Position(int generation, Vector3 absolute)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        if (generation >= BuiltinBlobLayout.FramedPositionWireGeneration)
        {
            bw.Write(WorldFrame.Origin.X);
            bw.Write(WorldFrame.Origin.Z);
        }
        bw.Write(absolute.X); bw.Write(absolute.Y); bw.Write(absolute.Z);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>What the display-name codec writes: <c>[ushort byteLen][byteLen UTF-8 bytes]</c>, unchanged at every
    /// generation.</summary>
    internal static byte[] Identity(string displayName)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(displayName);
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((ushort)utf8.Length);
        bw.Write(utf8);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>What the dynamic-body codec writes: the orientation quaternion then linear and angular velocity.
    /// Unchanged at every generation since the component landed.</summary>
    internal static byte[] DynamicBody(Quaternion orientation, Vector3 linear, Vector3 angular)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(orientation.X); bw.Write(orientation.Y); bw.Write(orientation.Z); bw.Write(orientation.W);
        bw.Write(linear.X); bw.Write(linear.Y); bw.Write(linear.Z);
        bw.Write(angular.X); bw.Write(angular.Y); bw.Write(angular.Z);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>What the pickup codec writes, from wire generation
    /// <see cref="BuiltinBlobLayout.PickupWireGeneration"/> on: the payload id then the owner net id.</summary>
    internal static byte[] Pickup(long payloadId, long ownerNetId)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(payloadId);
        bw.Write(ownerNetId);
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>Assembles a snapshot body: <c>[count][per entity: netId + (typeId, payload).. + 0]</c>. Entity ids are
    /// written 32-bit when the ctor's <c>netId32</c> is set (the pre-10.0.0 cell-blob schema v1 shape), 64-bit
    /// otherwise.</summary>
    internal sealed class BodyBuilder
    {
        private readonly bool netId32;
        private readonly List<(long netId, List<(ushort typeId, byte[] payload)> comps)> entities = new();

        internal BodyBuilder(bool netId32 = false) => this.netId32 = netId32;

        internal BodyBuilder Entity(long netId, params (ushort TypeId, byte[] Payload)[] components)
        {
            entities.Add((netId, new List<(ushort, byte[])>(components)));
            return this;
        }

        internal byte[] ToBody()
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write(entities.Count);
            foreach ((long netId, List<(ushort typeId, byte[] payload)> comps) in entities)
            {
                if (netId32) bw.Write((int)netId); else bw.Write(netId);
                foreach ((ushort typeId, byte[] payload) in comps)
                {
                    bw.Write(typeId);
                    bw.Write(payload);
                }
                bw.Write((ushort)0);   // end-of-entity terminator
            }
            bw.Flush();
            return ms.ToArray();
        }
    }

    /// <summary>Wraps a body in the cell-blob header the driver reads: <c>[magic][schemaVersion]</c>, plus the
    /// <c>[wireGeneration]</c> word from <see cref="WireGenerationBlobMigration.StampedSchemaVersion"/> on. Mirrors
    /// <c>CellPersistence.Wrap</c>, which is private, so a test can seed a blob at a schema/generation pair no build
    /// in this repo writes any more.</summary>
    internal static byte[] Wrap(int schemaVersion, int wireGeneration, byte[] body)
    {
        bool stamped = schemaVersion >= WireGenerationBlobMigration.StampedSchemaVersion;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(0x3150434B);   // "KCP1"
        bw.Write(schemaVersion);
        if (stamped) bw.Write(wireGeneration);
        bw.Write(body);
        bw.Flush();
        return ms.ToArray();
    }
}
