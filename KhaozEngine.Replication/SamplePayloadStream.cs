using System;
using System.IO;

namespace KhaozEngine.Replication;

/// <summary>A window over one stored sample for the duration of a codec callback.</summary>
internal sealed class SamplePayloadStream() : BorrowedPayloadStream(nameof(SamplePayloadStream))
{
    public override void CopyTo(Stream destination, int bufferSize) => throw new NotSupportedException();
}
