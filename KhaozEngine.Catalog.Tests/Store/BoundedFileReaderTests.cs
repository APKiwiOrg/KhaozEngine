using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Store;

public class BoundedFileReaderTests
{
    [Fact]
    public async Task ReadAsync_answers_null_when_the_stream_grew_after_its_length_was_sampled()
    {
        await using var stream = new MemoryStream([0x01, 0x02]);

        ReadOnlyMemory<byte>? bytes = await BoundedFileReader.ReadAsync(
            stream,
            observedLength: 1,
            maxBytes: 1);

        Assert.Null(bytes);
    }
}
