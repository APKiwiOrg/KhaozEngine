namespace KhaozEngine.MapDoc.Storage;

/// <summary>Observes payload attempts and live payloads without controlling whole-identity traversal.</summary>
internal sealed class MapWholeIdentityWork
{
    int _payloadsHeld;
    internal int PayloadReads { get; private set; }
    internal int MaximumPayloadsHeld { get; private set; }

    internal void BeforePayloadRead() => PayloadReads++;
    internal void HoldPayload()
    {
        _payloadsHeld++;
        MaximumPayloadsHeld = System.Math.Max(MaximumPayloadsHeld, _payloadsHeld);
    }
    internal void ReleasePayload() => _payloadsHeld--;
}
