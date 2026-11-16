namespace KhaozEngine.TileWorld.Physics;

// Flat traces and larger prism results do not enlarge any of the 128 small-memo slots.
internal sealed class TileWaterTraceResultMemo : TileWaterTraceThreadTables<TileWaterTraceResultMemo.Table>
{
    internal const int EntryLimit = 8, SpanLimit = 64, ContactLimit = 256;
    internal const int ByteLimit = 256 * 1024, IdentifierByteLimit = 16 * 1024;
    protected override Table CreateTable() => new();

    internal sealed class Table(int byteBudget = ByteLimit, int identifierBudget = IdentifierByteLimit)
        : TileWaterTraceStorage(EntryLimit, SpanLimit, ContactLimit, byteBudget, identifierBudget)
    {
        internal static int BucketForTests(in TileWaterTraceMemo.Key key) => Bucket(key, EntryLimit * 2);
    }
}
