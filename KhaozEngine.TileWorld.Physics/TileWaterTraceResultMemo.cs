using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

// Test-first boundary. The bounded implementation follows its red contract tests.
internal sealed class TileWaterTraceResultMemo
{
    internal const int EntryLimit = 8, SpanLimit = 64, ContactLimit = 256;
    internal const int ByteLimit = 256 * 1024, IdentifierByteLimit = 16 * 1024;
    internal sealed class Table
    {
        internal Table(int byteBudget = ByteLimit, int identifierBudget = IdentifierByteLimit)
        { _ = byteBudget; _ = identifierBudget; }
        internal int CountForTests => 0;
        internal int OwnedBytes => 0;
        internal int IdentifierBytes => 0;
        internal static int BucketForTests(in TileWaterTraceMemo.Key key) => key.GetHashCode() & 15;
        internal bool TryRead(in TileWaterTraceMemo.Key key, in MovementSpaceKey space, in MovementQueryIdentity identity,
            Span<MovementCoverageSpan> spans, Span<MovementDomainContact> contacts, out MovementCoverageResult result)
        { result = default; return false; }
        internal void Store(in TileWaterTraceMemo.Key key, in MovementCoverageResult result,
            ReadOnlySpan<MovementCoverageSpan> spans, ReadOnlySpan<MovementDomainContact> contacts) { }
        internal void Clear() { }
    }
}
