using System;
using System.IO;
using KhaozEngine.Catalog;

namespace KhaozEngine.Tests.Catalog.AzureBlob;

/// <summary>
/// The conformance suite over the incumbent provider, which is what makes it a CONTRACT rather than a
/// description of the new store. A fact the file system store fails is a fact about the suite.
/// </summary>
public sealed class FileSystemPackStoreConformanceTests : PackStoreConformance
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kec-blob-conformance-" + Guid.NewGuid().ToString("n"));

    /// <inheritdoc />
    protected override IPackStore CreateStore() => new FileSystemPackStore(_root);

    /// <inheritdoc />
    public override void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a green test over.
        }

        base.Dispose();
    }
}
