using System.Text;

namespace KhaozEngine.Replication;

// Each reused reader owns its decoder. Reset it when a borrow ends so an incomplete
// UTF-8 sequence cannot carry over to the next input.
internal sealed class ResettableReadEncoding : UTF8Encoding
{
    private Decoder? decoder;

    public override Decoder GetDecoder() => decoder ??= base.GetDecoder();

    internal void ResetDecoder() => decoder?.Reset();
}
