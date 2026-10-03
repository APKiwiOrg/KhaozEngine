using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;

namespace KhaozEngine.Tests.Catalog;

/// <summary>
/// The text conformance suite against <see cref="InMemoryContentAuthoringStore"/>, the REFERENCE behaviour
/// every provider is compared with. It writes a real pack to a temp directory, because a publish writes files
/// before it commits.
/// </summary>
public sealed class InMemoryContentAuthoringTextConformanceTests : ContentAuthoringTextStoreConformance, IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "kec-text-conformance-" + Guid.NewGuid().ToString("n"));
    int _packs;
    bool _faultArmed;

    /// <inheritdoc />
    protected override async Task<IContentTextAuthoringStore> OpenAsync(Func<DateTimeOffset>? clock = null)
    {
        _packs++;
        Func<DateTimeOffset> read = clock ?? (static () => DateTimeOffset.UtcNow);
        var store = new InMemoryContentAuthoringStore(
            TextRegistry(),
            new FileSystemPackStore(Path.Combine(_root, "pack-" + _packs.ToString(CultureInfo.InvariantCulture))),
            () => _faultArmed ? throw new InvalidOperationException("The audit write failed, the injected fault.") : read());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        return store;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Through the CLOCK, which every audit entry is stamped from, so a clock that throws is an audit write
    /// that fails. It also fails the draft's opening, which the reference builds in the same locals.
    /// </remarks>
    protected override IDisposable ArmATextAuditFault(IContentTextAuthoringStore store)
    {
        _faultArmed = true;
        return new Disarm(this);
    }

    /// <inheritdoc />
    protected override IPackStore PackOf(IContentTextAuthoringStore store)
        => store is InMemoryContentAuthoringStore { PackStore: IPackStore pack }
            ? pack
            : throw new InvalidOperationException("Every store here is opened over a pack directory of its own.");

    /// <inheritdoc />
    public void Dispose()
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
    }

    sealed class Disarm(InMemoryContentAuthoringTextConformanceTests owner) : IDisposable
    {
        public void Dispose() => owner._faultArmed = false;
    }
}
