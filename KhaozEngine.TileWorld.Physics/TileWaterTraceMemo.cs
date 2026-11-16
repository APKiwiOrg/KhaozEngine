using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

// Owned by one immutable sampler. Small repeated Contact paths keep their original bounds.
internal sealed class TileWaterTraceMemo : TileWaterTraceThreadTables<TileWaterTraceMemo.Table>
{
    internal const int EntryLimit = 128, SpanLimit = 8, ContactLimit = 48;
    internal const int SpaceIdCharacterLimit = 256;
    protected override Table CreateTable() => new();

    // Integer representations preserve signed zero. Space strings compare ordinally by value.
    internal readonly record struct Key(int X, int Y, int Z, int Dx, int Dy, int Dz, int Radius,
        int HalfHeight, int Ox, int Oy, int Oz, MovementSpaceKey Space, int Spans, int Contacts, ulong Fp)
    {
        internal Key(in MovementMediumSweepQuery query, Vector3 origin, int spans, int contacts, ulong fp)
            : this(Bits(query.Body.Centre.X), Bits(query.Body.Centre.Y), Bits(query.Body.Centre.Z),
                Bits(query.Delta.X), Bits(query.Delta.Y), Bits(query.Delta.Z), Bits(query.Body.Radius),
                Bits(query.Body.HalfHeight), Bits(origin.X), Bits(origin.Y), Bits(origin.Z),
                query.Body.CurrentSpace, spans, contacts, fp)
        { }
        static int Bits(float value) => BitConverter.SingleToInt32Bits(value);
    }

    internal sealed class Table() : TileWaterTraceStorage(EntryLimit, SpanLimit, ContactLimit)
    {
        internal static int BucketForTests(in Key key) => Bucket(key, EntryLimit * 2);
    }
}
